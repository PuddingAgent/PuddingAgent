using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// 池化子代理的状态枚举。
/// </summary>
public enum PooledSubAgentStatus
{
    /// <summary>已创建，尚未执行。</summary>
    Idle,
    /// <summary>正在执行任务。</summary>
    Busy,
    /// <summary>执行完毕，空闲等待复用。</summary>
    Sleeping,
    /// <summary>已销毁，不可复用。</summary>
    Dead,
}

/// <summary>
/// 池化子代理的信息快照（只读视图）。
/// </summary>
public sealed record PooledSubAgent
{
    /// <summary>池内唯一名称。</summary>
    public required string Name { get; init; }
    /// <summary>子代理会话 ID（复用同一 ID 以保持上下文/KV-cache）。</summary>
    public required string SubSessionId { get; init; }
    /// <summary>代理模板 ID。</summary>
    public required string TemplateId { get; init; }
    /// <summary>角色描述（可选）。</summary>
    public string? Role { get; init; }
    /// <summary>创建时间。</summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>最后使用时间。</summary>
    public required DateTimeOffset LastUsedAt { get; init; }
    /// <summary>当前状态。</summary>
    public required PooledSubAgentStatus Status { get; init; }
    /// <summary>已执行任务数。</summary>
    public required int TaskCount { get; init; }
    /// <summary>最后一次执行是否成功。</summary>
    public bool? LastSuccess { get; init; }
}

/// <summary>
/// 子代理池 — 池化复用子代理以最大化 KV-cache 命中率。
///
/// 生命周期：
///   Create → Idle → Busy → Sleeping → Busy → Sleeping → ... → Dead
///
/// 线程安全：使用 ConcurrentDictionary + SemaphoreSlim 保护状态变更。
/// 向后兼容：不修改现有 spawn_sub_agent 行为，作为可选新增功能。
/// </summary>
public sealed class SubAgentPool
{
    private readonly ISubAgentManager _subAgentManager;
    private readonly ILogger<SubAgentPool> _logger;
    private readonly int _maxCapacity;

    /// <summary>
    /// 池内条目的可变内部状态。通过 SemaphoreSlim 保护状态变更。
    /// </summary>
    private sealed class PoolEntry
    {
        public string Name { get; }
        public string SubSessionId { get; }
        public string TemplateId { get; }
        public string? Role { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset LastUsedAt { get; set; }
        public PooledSubAgentStatus Status { get; set; }
        public int TaskCount { get; set; }
        public bool? LastSuccess { get; set; }

        public PoolEntry(string name, string subSessionId, string templateId, string? role)
        {
            Name = name;
            SubSessionId = subSessionId;
            TemplateId = templateId;
            Role = role;
            CreatedAt = DateTimeOffset.UtcNow;
            LastUsedAt = CreatedAt;
            Status = PooledSubAgentStatus.Idle;
            TaskCount = 0;
            LastSuccess = null;
        }

        public PooledSubAgent ToSnapshot() => new()
        {
            Name = Name,
            SubSessionId = SubSessionId,
            TemplateId = TemplateId,
            Role = Role,
            CreatedAt = CreatedAt,
            LastUsedAt = LastUsedAt,
            Status = Status,
            TaskCount = TaskCount,
            LastSuccess = LastSuccess,
        };
    }

    private readonly ConcurrentDictionary<string, PoolEntry> _pool = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    public SubAgentPool(
        ISubAgentManager subAgentManager,
        IConfiguration configuration,
        ILogger<SubAgentPool> logger)
    {
        _subAgentManager = subAgentManager;
        _logger = logger;

        var configCapacity = configuration["SubAgentPool:MaxCapacity"];
        if (int.TryParse(configCapacity, out var parsed) && parsed > 0)
        {
            _maxCapacity = parsed;
        }
        else
        {
            _maxCapacity = 15;
        }

        _logger.LogInformation(
            "[SubAgentPool] Initialized with MaxCapacity={MaxCapacity}", _maxCapacity);
    }

    /// <summary>当前池中活跃子代理数量（不含 Dead）。</summary>
    public int Count => _pool.Count;

    /// <summary>池容量上限。</summary>
    public int MaxCapacity => _maxCapacity;

    // ════════════════════════════════════════════════════════
    // 创建
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 创建命名子代理，初始化上下文。
    /// 池满时抛出 InvalidOperationException。
    /// 已存在同名子代理时抛出 InvalidOperationException。
    /// </summary>
    public async Task<PooledSubAgent> CreateAsync(
        string name,
        SubAgentSpawnRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Sub-agent pool name cannot be null or empty.", nameof(name));

        PoolEntry entry;
        await _stateLock.WaitAsync(ct);
        try
        {
            if (_pool.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Sub-agent '{name}' already exists in the pool.");
            }

            if (_pool.Count >= _maxCapacity)
            {
                throw new InvalidOperationException(
                    $"Sub-agent pool is full (capacity={_maxCapacity}). " +
                    $"Cannot create '{name}'. Consider calling DestroyAsync on unused agents.");
            }

            // 池创建只预留稳定会话标识，不创建 run，也不执行任务。
            // 真正执行统一由 ExecuteSyncAsync 完成，否则 create → execute 会对同一
            // SubSessionId 重叠启动两次，并污染当前状态投影与 runId 映射。
            entry = new PoolEntry(
                name: name,
                subSessionId: SubAgentSessionId.Create(request.ParentSessionId),
                templateId: request.TemplateId,
                role: request.RoleInPlan);
            _pool[name] = entry;
        }
        finally
        {
            _stateLock.Release();
        }

        _logger.LogInformation(
            "[SubAgentPool] Reserved sub-agent '{Name}' subSessionId={Sub} template={Template}",
            name, entry.SubSessionId, request.TemplateId);

        return entry.ToSnapshot();
    }

    // ════════════════════════════════════════════════════════
    // 获取
    // ════════════════════════════════════════════════════════

    /// <summary>获取指定子代理的信息快照。不存在时返回 null。</summary>
    public Task<PooledSubAgent?> GetAsync(string name, CancellationToken ct = default)
    {
        if (_pool.TryGetValue(name, out var entry))
        {
            return Task.FromResult<PooledSubAgent?>(entry.ToSnapshot());
        }
        return Task.FromResult<PooledSubAgent?>(null);
    }

    // ════════════════════════════════════════════════════════
    // 执行
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 向已有子代理发送新任务，追加到已有会话。
    /// 若子代理不存在则自动创建。
    /// 执行期间状态从 Sleeping/Idle → Busy，完成后 → Sleeping。
    /// </summary>
    public async Task<SubAgentExecuteResult> ExecuteAsync(
        string name,
        SubAgentSpawnRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Sub-agent pool name cannot be null or empty.", nameof(name));

        // 获取或创建池条目
        PoolEntry entry;
        bool autoReserved = false;

        await _stateLock.WaitAsync(ct);
        try
        {
            if (_pool.TryGetValue(name, out var existing))
            {
                if (existing.Status == PooledSubAgentStatus.Busy)
                {
                    throw new InvalidOperationException(
                        $"Sub-agent '{name}' is currently busy. Wait for the current task to complete.");
                }
                if (existing.Status == PooledSubAgentStatus.Dead)
                {
                    throw new InvalidOperationException(
                        $"Sub-agent '{name}' has been destroyed and cannot be reused.");
                }

                // 标记为 Busy（在锁内，确保状态变更原子性）
                existing.Status = PooledSubAgentStatus.Busy;
                existing.LastUsedAt = DateTimeOffset.UtcNow;
                entry = existing;
            }
            else
            {
                if (_pool.Count >= _maxCapacity)
                {
                    throw new InvalidOperationException(
                        $"Sub-agent pool is full (capacity={_maxCapacity}). " +
                        $"Cannot create '{name}'. Consider calling DestroyAsync on unused agents.");
                }

                entry = new PoolEntry(
                    name: name,
                    subSessionId: SubAgentSessionId.Create(request.ParentSessionId),
                    templateId: request.TemplateId,
                    role: request.RoleInPlan)
                {
                    Status = PooledSubAgentStatus.Busy,
                    LastUsedAt = DateTimeOffset.UtcNow,
                };
                _pool[name] = entry;
                autoReserved = true;
            }
        }
        finally
        {
            _stateLock.Release();
        }

        if (autoReserved)
        {
            _logger.LogInformation(
                "[SubAgentPool] Auto-reserved sub-agent '{Name}' subSessionId={Sub}",
                name, entry.SubSessionId);
        }

        // 构建复用会话的请求：保持相同的 SubSessionId 以复用会话历史
        // 通过 ReuseSubSessionId 传入 pool entry 的 SubSessionId 以复用会话
        var executeRequest = request with
        {
            ReuseSubSessionId = entry.SubSessionId,
        };

        SubAgentExecuteResult result;
        try
        {
            result = await _subAgentManager.ExecuteSyncAsync(executeRequest, ct);
        }
        catch (Exception ex)
        {
            // 执行失败，更新状态
            await _stateLock.WaitAsync(CancellationToken.None);
            try
            {
                if (_pool.TryGetValue(name, out var current))
                {
                    current.Status = PooledSubAgentStatus.Sleeping;
                    current.TaskCount++;
                    current.LastSuccess = false;
                    current.LastUsedAt = DateTimeOffset.UtcNow;
                }
            }
            finally
            {
                _stateLock.Release();
            }

            _logger.LogError(ex,
                "[SubAgentPool] Execute failed for '{Name}' subSessionId={Sub}",
                name, entry.SubSessionId);

            throw;
        }

        // 执行成功，更新状态
        await _stateLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_pool.TryGetValue(name, out var current))
            {
                current.Status = PooledSubAgentStatus.Sleeping;
                current.TaskCount++;
                current.LastSuccess = result.Success;
                current.LastUsedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _stateLock.Release();
        }

        _logger.LogInformation(
            "[SubAgentPool] Execute completed for '{Name}' success={Success} taskCount={TaskCount}",
            name, result.Success, entry.TaskCount);

        return result;
    }

    // ════════════════════════════════════════════════════════
    // 睡眠
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 标记子代理为空闲（Sleeping）。
    /// 不存在或已销毁时返回 false。
    /// </summary>
    public async Task<bool> SleepAsync(string name, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (!_pool.TryGetValue(name, out var entry))
            {
                _logger.LogWarning("[SubAgentPool] SleepAsync: '{Name}' not found", name);
                return false;
            }

            if (entry.Status == PooledSubAgentStatus.Dead)
            {
                _logger.LogWarning("[SubAgentPool] SleepAsync: '{Name}' is already dead", name);
                return false;
            }

            if (entry.Status == PooledSubAgentStatus.Busy)
            {
                _logger.LogWarning(
                    "[SubAgentPool] SleepAsync: '{Name}' is busy, force-sleeping", name);
            }

            entry.Status = PooledSubAgentStatus.Sleeping;
            entry.LastUsedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("[SubAgentPool] '{Name}' set to Sleeping", name);
            return true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    // ════════════════════════════════════════════════════════
    // 销毁
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 销毁子代理，释放资源。
    /// 不存在时返回 false（不抛异常）。
    /// </summary>
    public async Task<bool> DestroyAsync(string name, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (!_pool.TryRemove(name, out var entry))
            {
                _logger.LogWarning("[SubAgentPool] DestroyAsync: '{Name}' not found", name);
                return false;
            }

            entry.Status = PooledSubAgentStatus.Dead;

            _logger.LogInformation(
                "[SubAgentPool] Destroyed '{Name}' subSessionId={Sub} taskCount={TaskCount}",
                name, entry.SubSessionId, entry.TaskCount);

            return true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    // ════════════════════════════════════════════════════════
    // 列表
    // ════════════════════════════════════════════════════════

    /// <summary>列出池中所有子代理的状态快照。</summary>
    public IReadOnlyList<PooledSubAgent> List()
    {
        return _pool.Values
            .Select(e => e.ToSnapshot())
            .OrderByDescending(e => e.LastUsedAt)
            .ToList();
    }

    // ════════════════════════════════════════════════════════
    // LRU 淘汰（可选）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 尝试淘汰最久未使用的 Sleeping 子代理。
    /// 返回被淘汰的子代理名称，无可用候选时返回 null。
    /// </summary>
    public async Task<string?> EvictLeastRecentlyUsedAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            var candidate = _pool.Values
                .Where(e => e.Status == PooledSubAgentStatus.Sleeping)
                .OrderBy(e => e.LastUsedAt)
                .FirstOrDefault();

            if (candidate == null)
            {
                _logger.LogDebug("[SubAgentPool] EvictLRU: no sleeping candidate found");
                return null;
            }

            _pool.TryRemove(candidate.Name, out _);
            candidate.Status = PooledSubAgentStatus.Dead;

            _logger.LogInformation(
                "[SubAgentPool] Evicted LRU sub-agent '{Name}' lastUsed={LastUsed}",
                candidate.Name, candidate.LastUsedAt);

            return candidate.Name;
        }
        finally
        {
            _stateLock.Release();
        }
    }
}
