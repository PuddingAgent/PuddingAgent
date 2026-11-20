using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentPool;

namespace PuddingPlatform.Services;

/// <summary>
/// 子代理池 — 池化复用子代理以最大化 KV-cache 命中率。
///
/// 生命周期：
///   Create → Idle → Busy → Sleeping → Busy → Sleeping → ... → Dead
///
/// 线程安全：使用 ConcurrentDictionary + SemaphoreSlim 保护状态变更。
/// 向后兼容：不修改现有 spawn_sub_agent 行为，作为可选新增功能。
/// </summary>
public sealed class SubAgentPool : ISubAgentPool
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

        /// <summary>
        /// 本次执行的令牌。收尾时必须与它比对：同名条目可能在「置 Busy」与「收尾」之间
        /// 被 destroy → create 替换成另一个对象，迟到的收尾若按名字回查就会把结果写到
        /// 新条目上（D-P0-3 的 stale completion）。
        /// </summary>
        public string? ActiveExecutionId { get; set; }

        /// <summary>创建者父会话。持久化后条目会活过创建它的那次执行，归属必须落在这里。</summary>
        public string? OwnerSessionId { get; }

        /// <summary>创建者工作区。跨工作区复用同样会污染投影，故一并记录。</summary>
        public string? OwnerWorkspaceId { get; }

        public PoolEntry(string name, string subSessionId, string templateId, string? role, string? ownerSessionId, string? ownerWorkspaceId)
        {
            Name = name;
            SubSessionId = subSessionId;
            TemplateId = templateId;
            Role = role;
            OwnerSessionId = ownerSessionId;
            OwnerWorkspaceId = ownerWorkspaceId;
            CreatedAt = DateTimeOffset.UtcNow;
            LastUsedAt = CreatedAt;
            Status = PooledSubAgentStatus.Idle;
            TaskCount = 0;
            LastSuccess = null;
        }

        /// <summary>
        /// 从持久化快照重建条目（进程重启后把库里的池恢复成内存视图）。
        /// <para>
        /// 时间与计数全部以库为准：用 <c>UtcNow</c> 重建会让「创建于上周」的条目看起来是刚生的，
        /// LRU 选型、保留策略、诊断时间线都会因此错位。
        /// </para>
        /// </summary>
        public PoolEntry(
            string name,
            string subSessionId,
            string templateId,
            string? role,
            string? ownerSessionId,
            string? ownerWorkspaceId,
            DateTimeOffset createdAt,
            DateTimeOffset lastUsedAt,
            PooledSubAgentStatus status,
            int taskCount,
            bool? lastSuccess,
            string? activeExecutionId)
        {
            Name = name;
            SubSessionId = subSessionId;
            TemplateId = templateId;
            Role = role;
            OwnerSessionId = ownerSessionId;
            OwnerWorkspaceId = ownerWorkspaceId;
            CreatedAt = createdAt;
            LastUsedAt = lastUsedAt;
            Status = status;
            TaskCount = taskCount;
            LastSuccess = lastSuccess;
            ActiveExecutionId = activeExecutionId;
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

    /// <summary>
    /// 持久化存储。<c>null</c> 表示纯内存模式（既有行为，测试与未接存储的宿主都走这条路）。
    /// 非空时池必须先就绪才能读写：未就绪就去写，会与库里的既有条目撞唯一索引，
    /// 而那时候池本身还看不到那些条目。
    /// </summary>
    private readonly IAgentPoolEntryStore? _entryStore;

    /// <summary>一次性加载的临界区：并发调用只会真的读一次库。</summary>
    private readonly SemaphoreSlim _readyGate = new(1, 1);

    /// <summary>就绪状态（由 <see cref="EnsureReadyAsync"/> 驱动；无 store 时恒为 Ready）。</summary>
    private volatile PoolReadiness _readiness;

    /// <summary>上次加载失败的原始异常。失败是终态，错误文案里必须能说出原因。</summary>
    private Exception? _loadFailure;

    public SubAgentPool(
        ISubAgentManager subAgentManager,
        IConfiguration configuration,
        ILogger<SubAgentPool> logger,
        IAgentPoolEntryStore? entryStore = null)
    {
        _subAgentManager = subAgentManager;
        _logger = logger;
        _entryStore = entryStore;

        // 没有存储层就没有「加载」这回事：池一开始就是就绪的，
        // 这样既有的 29 个池用例与纯内存宿主无需任何改动。
        _readiness = entryStore is null ? PoolReadiness.Ready : PoolReadiness.NotStarted;

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

    /// <inheritdoc />
    public PoolReadiness Readiness => _readiness;

    // ════════════════════════════════════════════════════════
    // 就绪
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 确保池内视图已从存储加载（一次性；并发调用只真读一次库）。
    /// <para>
    /// <b>为什么失败不降级为空池：</b>池集合是 GC 的删除白名单（见 <see cref="PoolNotReadyException"/>）。
    /// 库读不到时“降级为空”会让 GC 把全部池化目录当成孤儿隔离，而“报错”只会让本轮扫描跳过。
    /// 两者的代价完全不对称，所以失败必须留成 <see cref="PoolReadiness.Failed"/> 并向上抛。
    /// </para>
    /// <para>
    /// 失败是<b>终态</b>（不自动重试）：库不可用是个环境故障，静默重试只会让每次调用都先超时一次，
    /// 而调用方从异常里拿不到“刚才已经失败过 20 次”这个信息。需要重试就重启进程。
    /// </para>
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        // 纯内存模式：没有“未就绪”这回事。
        if (_entryStore is null)
            return;

        // 快路径：已就绪直接返回；已失败不抢锁，但要把原因报出去。
        var observed = _readiness;
        if (observed == PoolReadiness.Ready)
            return;
        if (observed == PoolReadiness.Failed)
            throw BuildNotReadyException();

        await _readyGate.WaitAsync(ct);
        try
        {
            // 双检：等锁期间可能已被另一个调用方加载完成（或已失败）。
            if (_readiness == PoolReadiness.Ready)
                return;
            if (_readiness == PoolReadiness.Failed)
                throw BuildNotReadyException();

            _readiness = PoolReadiness.Loading;

            IReadOnlyList<AgentPoolEntrySnapshot> loaded;
            try
            {
                loaded = await _entryStore.LoadAllAsync(ct);
            }
            catch (Exception ex)
            {
                _loadFailure = ex;
                _readiness = PoolReadiness.Failed;
                _logger.LogError(ex, "[SubAgentPool] Failed to load persisted pool entries; pool marked Failed");
                throw new PoolNotReadyException(
                    $"[SubAgentPool] Pool recovery failed and will not be treated as an empty pool: " +
                    $"{ex.GetType().Name}: {ex.Message}",
                    PoolReadiness.Failed,
                    ex);
            }

            foreach (var snapshot in loaded)
            {
                // 用 TryAdd 而不是索引赋值：ExecuteAsync 的「自动预留」路径不经过就绪门，
                // 可能在加载期间已经建了一个同名条目并正在执行。那个条目带着活的子会话，
                // 用库里的快照覆盖它会把正在执行的收尾丢掉（FinishAsync 是引用相等判定）。
                _pool.TryAdd(snapshot.Name, new PoolEntry(
                    name: snapshot.Name,
                    subSessionId: snapshot.SubSessionId,
                    templateId: snapshot.TemplateId,
                    role: snapshot.Role,
                    ownerSessionId: snapshot.OwnerSessionId,
                    ownerWorkspaceId: snapshot.OwnerWorkspaceId,
                    createdAt: snapshot.CreatedAt,
                    lastUsedAt: snapshot.LastUsedAt,
                    status: MapStatus(snapshot.Status),
                    taskCount: snapshot.TaskCount,
                    lastSuccess: snapshot.LastSuccess,
                    activeExecutionId: snapshot.ActiveExecutionId));
            }

            _readiness = PoolReadiness.Ready;
            _logger.LogInformation(
                "[SubAgentPool] Restored {Count} pool entries from storage (readiness=Ready)", loaded.Count);
        }
        finally
        {
            _readyGate.Release();
        }
    }

    /// <summary>
    /// 库内状态串 → 运行时状态。未知值一律当成 <see cref="PooledSubAgentStatus.Dead"/>
    /// （不可复用），而不是猜成 Idle：认错方向的代价是「一个不该被复用的子代理被派活」。
    /// </summary>
    private static PooledSubAgentStatus MapStatus(string status) => status switch
    {
        AgentPoolEntity.StatusIdle => PooledSubAgentStatus.Idle,
        AgentPoolEntity.StatusBusy => PooledSubAgentStatus.Busy,
        AgentPoolEntity.StatusSleeping => PooledSubAgentStatus.Sleeping,
        AgentPoolEntity.StatusDead => PooledSubAgentStatus.Dead,
        _ => PooledSubAgentStatus.Dead,
    };

    private PoolNotReadyException BuildNotReadyException() => new(
        $"[SubAgentPool] Pool is not ready (readiness={_readiness}); " +
        $"refusing to report an empty pool. " +
        (_loadFailure is null
            ? "It has not finished loading yet."
            : $"Last load error: {_loadFailure.GetType().Name}: {_loadFailure.Message}"),
        _readiness,
        _loadFailure);

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

        // 先把视图从库加载好：没加载就写库，会与库里的既有条目撞唯一索引（name / sub_session_id），
        // 而那时池自己还看不到它们——错会变成“约束报错”，而不是“名字已存在”这种可理解的拒绝。
        await EnsureReadyAsync(ct);

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
                role: request.RoleInPlan,
                ownerSessionId: request.ParentSessionId,
                ownerWorkspaceId: request.WorkspaceId);

            if (_entryStore is not null)
            {
                // 先落库再进内存：写库失败时内存里不得留下「看起来存在但库里没有」的条目，
                // 否则重启后它会凭空消失，而调用方已经拿到了它的 SubSessionId。
                var inserted = await _entryStore.TryInsertAsync(
                    new AgentPoolInsertRequest(
                        PoolId: Guid.NewGuid().ToString("N"),
                        Name: name,
                        SubSessionId: entry.SubSessionId,
                        TemplateId: request.TemplateId,
                        Role: request.RoleInPlan,
                        OwnerWorkspaceId: request.WorkspaceId,
                        OwnerSessionId: request.ParentSessionId,
                        OwnerAgentId: request.ParentAgentId,
                        CreatedAt: entry.CreatedAt),
                    capacity: _maxCapacity,
                    ct);

                if (inserted == 0)
                {
                    // store 按库里的未软删行数判定池满。与内存池满用同一种异常与文案：
                    // 两种“满”对调用方的处置（先 destroy 不用的条目）完全一样。
                    throw new InvalidOperationException(
                        $"Sub-agent pool is full (capacity={_maxCapacity}). " +
                        $"Cannot create '{name}'. Consider calling DestroyAsync on unused agents.");
                }
            }

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
    /// 执行收尾：只在「条目仍是被执行的那个对象」且「执行令牌仍匹配」时应用结果。
    /// <para>
    /// 判据刻意不用名字：同名条目可能在执行期间被 destroy → create 换成另一个对象，
    /// 此时按名字回查会把旧执行的结果写到新条目上（task_count 被加、last_success 被覆盖、
    /// status 被打回 Sleeping）。引用相等 + 令牌双判，任一不符即丢弃并记一条 warning。
    /// </para>
    /// </summary>
    /// <summary>异常与日志文案里只展示会话 id 前 8 位，避免整串 id 刷屏。</summary>
    private static string Shorten(string value) =>
        value.Length <= 8 ? value : value[..8];

    private async Task FinishAsync(
        string name,
        PoolEntry executedEntry,
        string executionId,
        bool success,
        CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (!_pool.TryGetValue(name, out var current) ||
                !ReferenceEquals(current, executedEntry) ||
                !string.Equals(current.ActiveExecutionId, executionId, StringComparison.Ordinal))
            {
                // stale completion：丢弃，不碰任何计数与状态
                _logger.LogWarning(
                    "[SubAgentPool] Stale completion dropped name={Name} sub={Sub} exec={Exec}",
                    name, executedEntry.SubSessionId, executionId);
                return;
            }

            current.Status = PooledSubAgentStatus.Sleeping;
            current.TaskCount++;
            current.LastSuccess = success;
            current.LastUsedAt = DateTimeOffset.UtcNow;
            current.ActiveExecutionId = null;
        }
        finally
        {
            _stateLock.Release();
        }
    }

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

        // 每次执行生成一个令牌，收尾时与条目上的令牌比对；不匹配即视为迟到收尾并丢弃。
        var executionId = Guid.NewGuid().ToString("N");

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

                // 归属校验（D-P0-1）：只允许创建者父会话在同一工作区驱动该条目。
                // 必须放在置 Busy 之前——跨父复用会先建 run、再在会话投影上抛错，
                // 那时只能把 run 终结成 failed 并污染池计数，无法撤回。
                var ownerSession = existing.OwnerSessionId;
                if (!string.IsNullOrEmpty(ownerSession) &&
                    !string.Equals(ownerSession, request.ParentSessionId, StringComparison.Ordinal))
                {
                    throw new PoolOwnershipMismatchException(
                        $"Sub-agent '{name}' belongs to parent session '{Shorten(ownerSession)}' " +
                        $"but was requested by '{Shorten(request.ParentSessionId)}'.",
                        name, ownerSession, request.ParentSessionId,
                        PoolOwnershipMismatchException.OwnerSessionReason);
                }

                var ownerWorkspace = existing.OwnerWorkspaceId;
                if (!string.IsNullOrEmpty(ownerWorkspace) &&
                    !string.Equals(ownerWorkspace, request.WorkspaceId, StringComparison.Ordinal))
                {
                    throw new PoolOwnershipMismatchException(
                        $"Sub-agent '{name}' belongs to workspace '{ownerWorkspace}' " +
                        $"but was requested from '{request.WorkspaceId}'.",
                        name, ownerSession, request.ParentSessionId,
                        PoolOwnershipMismatchException.OwnerWorkspaceReason);
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
                    role: request.RoleInPlan,
                    ownerSessionId: request.ParentSessionId,
                    ownerWorkspaceId: request.WorkspaceId)
                {
                    Status = PooledSubAgentStatus.Busy,
                    LastUsedAt = DateTimeOffset.UtcNow,
                };
                _pool[name] = entry;
                autoReserved = true;
            }

            // 置 Busy 的同时写入执行令牌：收尾必须凭它判定「这次执行是否还属于这个条目」。
            entry.ActiveExecutionId = executionId;
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
            // 执行失败，更新状态（迟到的收尾会被 FinishAsync 丢弃）
            await FinishAsync(name, entry, executionId, success: false, CancellationToken.None);

            _logger.LogError(ex,
                "[SubAgentPool] Execute failed for '{Name}' subSessionId={Sub}",
                name, entry.SubSessionId);

            throw;
        }

        // 执行成功，更新状态（迟到的收尾会被 FinishAsync 丢弃）
        await FinishAsync(name, entry, executionId, success: result.Success, CancellationToken.None);

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
                // 与缺陷 D1 同型：busy 时只改 Status=Sleeping 会让执行继续在后台烧 token，
                // 而日志里的 force-sleeping 会让人以为已经停下（日志在说谎）。
                // 先真取消执行链，再标记休眠。
                _logger.LogInformation(
                    "[SubAgentPool] SleepAsync: '{Name}' is busy, cancelling the in-flight run before sleeping", name);
                await _subAgentManager.CancelAsync(
                    entry.SubSessionId,
                    "Pooled sub-agent put to sleep",
                    ct);
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

            var wasBusy = entry.Status == PooledSubAgentStatus.Busy;
            entry.Status = PooledSubAgentStatus.Dead;

            // 同 SleepAsync：条目已死并不代表执行循环已停。先真取消，避免「已销毁但仍在烧 token」。
            if (wasBusy)
            {
                await _subAgentManager.CancelAsync(
                    entry.SubSessionId,
                    "Pooled sub-agent destroyed",
                    ct);
            }

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

    /// <summary>
    /// 列出池中所有子代理的状态快照。
    /// <para>
    /// <b>未就绪时必须抛错，不能返回空集合：</b>本方法的调用方之一是
    /// <c>SubAgentTransientDirectoryGcService</c>，它拿这个集合当「哪些目录受池保护」的删除白名单。
    /// 「还没加载完」返回空集合 = 告诉 GC「没有任何池化目录需要保护」，那些目录会被当成孤儿隔离。
    /// 把「不知道」伪装成「没有」会直接导致丢数据；抛错只会让本轮扫描跳过。
    /// </para>
    /// </summary>
    public IReadOnlyList<PooledSubAgent> List()
    {
        if (_readiness != PoolReadiness.Ready)
            throw BuildNotReadyException();

        return _pool.Values
            .Select(e => e.ToSnapshot())
            .OrderByDescending(e => e.LastUsedAt)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 与 <see cref="List"/> 的差别只在「会不会先等加载」：同步方法无法 await，
    /// 所以需要「等到就绪再取」的调用方用这个。未就绪且加载失败时同样抛错，
    /// 不回退成空集合。
    /// </remarks>
    public async Task<IReadOnlyList<PooledSubAgent>> GetSnapshotAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        return List();
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
