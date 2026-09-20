using System.Text.Json;
using PuddingCode.Configuration;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services;

/// <summary>
/// Request registered by an agent via the <c>sleep</c> tool.
/// </summary>
public sealed class WakeRequest
{
    public string AgentId { get; init; } = "";
    public DateTime EnqueuedAt { get; init; } = DateTime.UtcNow;
    public TimeSpan MinIdle { get; init; }
    public TimeSpan MaxIdle { get; init; }
    public DateTime EarliestWakeAt { get; init; }
    public DateTime LatestWakeAt { get; init; }
}

/// <summary>
/// Multicast-agent wake queue that ensures only one agent is woken per idle cycle,
/// avoiding concurrent LLM calls during quiet periods.
/// 
/// Agents register via <c>sleep</c> → <see cref="EnqueueAsync"/>.
/// The <c>HeartbeatOrchestrator</c> calls <see cref="TryDequeueAsync"/> on each
/// idle-tick to pop the next ready agent.
/// 
/// Thread-safe: all public methods acquire an internal semaphore.
/// </summary>
public sealed class AgentWakeQueue
{
        // ── 系统默认心跳参数（1小时）──
    private static readonly TimeSpan DefaultMinIdle = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultMaxIdle = TimeSpan.FromHours(1);

    // ── 心跳重试指数退避延迟（30s → 60s → 120s）──
    private static readonly int[] RetryDelaySeconds = [30, 60, 120];
    private const int MaxRetryCount = 3;

    private readonly PriorityQueue<WakeRequest, DateTime> _queue = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<AgentWakeQueue> _logger;

    private readonly PuddingDataPaths? _paths;
    private readonly TimeProvider _clock;

    public AgentWakeQueue(ILogger<AgentWakeQueue> logger, PuddingDataPaths? paths = null, TimeProvider? clock = null)
    {
        _logger = logger;
        _paths = paths;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Register (or replace) an agent's wake request.  Called by the <c>sleep</c> tool.
    /// Existing entry for the same agent is removed first.
    /// </summary>
    public async Task EnqueueAsync(
        string agentId,
        TimeSpan minIdle,
        TimeSpan maxIdle,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var request = new WakeRequest
            {
                AgentId = agentId,
                EnqueuedAt = now,
                MinIdle = minIdle,
                MaxIdle = maxIdle,
                EarliestWakeAt = now.Add(minIdle),
                LatestWakeAt = now.Add(maxIdle),
            };

            await PersistLockedAsync(request, ct);
            RemoveLocked(agentId);
            // Order by LatestWakeAt — soonest-deadline first
            _queue.Enqueue(request, request.LatestWakeAt);

            _logger.LogDebug(
                "[AgentWakeQueue] Enqueued agent={Agent} min={Min}s max={Max}s depth={Depth}",
                agentId,
                minIdle.TotalSeconds.ToString("F0"),
                maxIdle.TotalSeconds.ToString("F0"),
                _queue.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Attempt to dequeue the next ready agent.  Returns null if no agent is
    /// ready (idle hasn't been long enough) or the queue is empty.
    /// </summary>
    public async Task<WakeRequest?> TryDequeueAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_queue.Count == 0) return null;

            var now = _clock.GetUtcNow().UtcDateTime;

            // 队列按 LatestWakeAt 排序（最晚可唤醒时间早者优先），而「是否到期」由
            // EarliestWakeAt 判定 —— 两个键不同，只检查队首会让「优先级最高但尚未
            // 到期」的条目阻塞其后所有已到期条目（head-of-line blocking）。
            // 这里整体扫描，取「已到期且 EarliestWakeAt 最早」的一条；
            // 未被选中的条目按原优先级回填，队列内容与顺序不变。
            var remaining = new List<(WakeRequest Request, DateTime Priority)>();
            WakeRequest? ready = null;
            var readyPriority = default(DateTime);

            while (_queue.TryDequeue(out var candidate, out var priority))
            {
                var isBetter = candidate.EarliestWakeAt <= now
                    && (ready is null
                        || candidate.EarliestWakeAt < ready.EarliestWakeAt
                        || (candidate.EarliestWakeAt == ready.EarliestWakeAt
                            && priority < readyPriority));

                if (isBetter)
                {
                    if (ready is not null)
                        remaining.Add((ready, readyPriority));
                    ready = candidate;
                    readyPriority = priority;
                }
                else
                {
                    remaining.Add((candidate, priority));
                }
            }

            foreach (var (pending, priority) in remaining)
                _queue.Enqueue(pending, priority);

            if (ready is null)
            {
                // 没有任何条目到期 — 尊重各自的 min_idle
                return null;
            }

            // Keep the durable due entry until delivery/deferral schedules the next cycle.
            _logger.LogInformation(
                "[AgentWakeQueue] Dequeued agent={Agent} idle={Idle}s min={Min}s max={Max}s depth={Depth}",
                ready.AgentId,
                ((int)(now - ready.EnqueuedAt).TotalSeconds).ToString(),
                ready.MinIdle.TotalSeconds.ToString("F0"),
                ready.MaxIdle.TotalSeconds.ToString("F0"),
                _queue.Count);

            return ready;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Remove a specific agent from the queue (e.g. agent went offline).
    /// </summary>
    public async Task RemoveAsync(string agentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            DeleteScheduleLocked(agentId);
            RemoveLocked(agentId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Return the current queue depth for diagnostics.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return _queue.Count; }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Called by the orchestrator when a user message arrives for any agent —
    /// clears that agent's sleep registration since the user message overrides it.
    /// </summary>
    public async Task NotifyUserActivityAsync(string agentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            DeleteScheduleLocked(agentId);
            RemoveLocked(agentId);
            _logger.LogDebug("[AgentWakeQueue] Cleared sleep for agent={Agent} due to user activity", agentId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 确保 agent 在队列中。若尚未注册则使用系统默认参数（1 小时）。
    /// 若已在内存队列中则不覆盖；重启后优先恢复持久化到期时间。
    /// </summary>
    public Task EnsureDefaultAsync(string agentId, CancellationToken ct = default)
        => EnsureScheduledAsync(agentId, DefaultMinIdle, DefaultMaxIdle, ct);

    /// <summary>Restore an existing durable deadline, or establish a first schedule.</summary>
    public Task EnsureScheduledAsync(string agentId, TimeSpan minIdle, TimeSpan maxIdle, CancellationToken ct = default)
        => EnsureScheduledCoreAsync(agentId, minIdle, maxIdle, restore: true, ct);

    /// <summary>After delivery, start the next cycle unless sleep already scheduled it.</summary>
    public Task ScheduleNextAsync(string agentId, TimeSpan minIdle, TimeSpan maxIdle, CancellationToken ct = default)
        => EnsureScheduledCoreAsync(agentId, minIdle, maxIdle, restore: false, ct);

    private async Task EnsureScheduledCoreAsync(string agentId, TimeSpan minIdle, TimeSpan maxIdle, bool restore, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (IsInQueueLocked(agentId)) return;
            var request = restore ? await ReadScheduleLockedAsync(agentId, ct) : null;
            if (request is null)
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                request = new WakeRequest
                {
                    AgentId = agentId, EnqueuedAt = now, MinIdle = minIdle, MaxIdle = maxIdle,
                    EarliestWakeAt = now.Add(minIdle), LatestWakeAt = now.Add(maxIdle),
                };
                await PersistLockedAsync(request, ct);
            }
            _queue.Enqueue(request, request.LatestWakeAt);
            _logger.LogInformation("[AgentWakeQueue] Scheduled agent={Agent} earliest={Earliest:o} latest={Latest:o}",
                agentId, request.EarliestWakeAt, request.LatestWakeAt);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 清理该 Agent 的内存和持久化调度（显式下线/停用）。
    /// </summary>
    public async Task ClearAsync(string agentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            DeleteScheduleLocked(agentId);
            RemoveLocked(agentId);
            _logger.LogDebug("[AgentWakeQueue] Cleared queue for agent={Agent}", agentId);
        }
        finally { _gate.Release(); }
    }

    // ── must be called inside _gate ──
    private void RemoveLocked(string agentId)
    {
        // PriorityQueue doesn't support remove-by-key, so we rebuild
        if (_queue.Count == 0) return;

        var kept = new List<(WakeRequest, DateTime)>();
        while (_queue.TryDequeue(out var req, out var pri))
        {
            if (!string.Equals(req.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                kept.Add((req, pri));
        }

        foreach (var (req, pri) in kept)
            _queue.Enqueue(req, pri);
    }

    /// <summary>Must be called inside _gate.</summary>
    private bool IsInQueueLocked(string agentId)
        => _queue.UnorderedItems.Any(item => string.Equals(item.Element.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 检查指定 agent 当前是否在唤醒队列中（线程安全）。
    /// </summary>
    public async Task<bool> IsInQueueAsync(string agentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return IsInQueueLocked(agentId); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 获取指定 agent 在队列中的唤醒请求详情（若存在）。只读遍历，不修改队列状态。
    /// </summary>
    public async Task<WakeRequest?> GetWakeRequestAsync(string agentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _queue.UnorderedItems.Select(item => item.Element)
                .FirstOrDefault(item => string.Equals(item.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        }
                finally { _gate.Release(); }
    }

    /// <summary>
    /// 心跳发送失败后重新入队，使用指数退避延迟。
    /// 最多重试 3 次；延迟依次为 30s、60s、120s。
    /// </summary>
    public async Task<bool> EnqueueRetryAsync(
        string agentId,
        int retryCount,
        CancellationToken ct = default)
    {
        if (retryCount >= MaxRetryCount)
        {
            _logger.LogWarning(
                "[AgentWakeQueue] Max retries ({Max}) exceeded for agent={Agent}; dropping heartbeat",
                MaxRetryCount, agentId);
            return false;
        }

        // 指数退避：30s → 60s → 120s
        var delaySeconds = RetryDelaySeconds[Math.Min(retryCount, RetryDelaySeconds.Length - 1)];
        var retryDelay = TimeSpan.FromSeconds(delaySeconds);

        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var request = new WakeRequest
            {
                AgentId = agentId,
                EnqueuedAt = now,
                MinIdle = retryDelay,
                MaxIdle = retryDelay,
                EarliestWakeAt = now.Add(retryDelay),
                LatestWakeAt = now.Add(retryDelay),
            };
            await PersistLockedAsync(request, ct);
            RemoveLocked(agentId);
            _queue.Enqueue(request, request.LatestWakeAt);

            _logger.LogInformation(
                "[AgentWakeQueue] Retry #{RetryCount} enqueued agent={Agent} delay={Delay}s",
                retryCount + 1, agentId, delaySeconds);
            return true;
        }
        finally { _gate.Release(); }
    }
    private string? SchedulePath(string agentId)
        => _paths is null ? null : Path.Combine(_paths.AgentInstanceRoot(agentId), "state", "heartbeat-wake.json");

    private async Task<WakeRequest?> ReadScheduleLockedAsync(string agentId, CancellationToken ct)
    {
        var path = SchedulePath(agentId);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var request = JsonSerializer.Deserialize<WakeRequest>(await File.ReadAllTextAsync(path, ct));
            if (request is null || !string.Equals(request.AgentId, agentId, StringComparison.OrdinalIgnoreCase)
                || request.EarliestWakeAt == default || request.LatestWakeAt < request.EarliestWakeAt)
                throw new JsonException("Invalid heartbeat wake schedule");
            return request;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[AgentWakeQueue] Invalid schedule for agent={Agent}; starting a fresh interval", agentId);
            return null;
        }
    }

    private async Task PersistLockedAsync(WakeRequest request, CancellationToken ct)
    {
        var path = SchedulePath(request.AgentId);
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(request), ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void DeleteScheduleLocked(string agentId)
    {
        var path = SchedulePath(agentId);
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

}
