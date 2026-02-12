using System.Collections.Concurrent;
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

    private readonly PriorityQueue<WakeRequest, DateTime> _queue = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<AgentWakeQueue> _logger;

    // 追踪调用过 sleep 的 agent，确保不被默认参数覆盖
    private readonly ConcurrentDictionary<string, bool> _customSleepAgents = new(StringComparer.OrdinalIgnoreCase);

    public AgentWakeQueue(ILogger<AgentWakeQueue> logger)
    {
        _logger = logger;
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
            // Remove existing entry for this agent (if any)
            RemoveLocked(agentId);

            // 标记为自定义 sleep，后续 EnsureDefaultAsync 不会覆盖
            _customSleepAgents[agentId] = true;

            var now = DateTime.UtcNow;
            var request = new WakeRequest
            {
                AgentId = agentId,
                EnqueuedAt = now,
                MinIdle = minIdle,
                MaxIdle = maxIdle,
                EarliestWakeAt = now.Add(minIdle),
                LatestWakeAt = now.Add(maxIdle),
            };

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

            if (!_queue.TryPeek(out var request, out _))
                return null;

            var now = DateTime.UtcNow;
            if (now < request.EarliestWakeAt)
            {
                // Not idle long enough yet — respect the agent's min_idle
                return null;
            }

            _queue.Dequeue();
            // 清理自定义标记 — 该条目的自定义周期已结束，后续心跳由默认机制接管
            _customSleepAgents.TryRemove(request.AgentId, out _);
            _logger.LogInformation(
                "[AgentWakeQueue] Dequeued agent={Agent} idle={Idle}s min={Min}s max={Max}s depth={Depth}",
                request.AgentId,
                ((int)(now - request.EnqueuedAt).TotalSeconds).ToString(),
                request.MinIdle.TotalSeconds.ToString("F0"),
                request.MaxIdle.TotalSeconds.ToString("F0"),
                _queue.Count);

            return request;
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
    /// 若自定义 sleep 条目仍在队列中则不覆盖；若已被消费则允许默认心跳接续。
    /// </summary>
    public async Task EnsureDefaultAsync(string agentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // 已在队列中（由自定义 sleep 或默认入队）→ 不重复入队
            if (IsInQueueLocked(agentId))
                return;

            // 自定义标记存在但队列已空 → 清除过期标记，允许默认心跳
            _customSleepAgents.TryRemove(agentId, out _);

            var now = DateTime.UtcNow;
            var request = new WakeRequest
            {
                AgentId = agentId,
                EnqueuedAt = now,
                MinIdle = DefaultMinIdle,
                MaxIdle = DefaultMaxIdle,
                EarliestWakeAt = now.Add(DefaultMinIdle),
                LatestWakeAt = now.Add(DefaultMaxIdle),
            };
            _queue.Enqueue(request, request.LatestWakeAt);

            _logger.LogDebug(
                "[AgentWakeQueue] Default-enqueued agent={Agent} min={Min}s max={Max}s depth={Depth}",
                agentId,
                DefaultMinIdle.TotalSeconds.ToString("F0"),
                DefaultMaxIdle.TotalSeconds.ToString("F0"),
                _queue.Count);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 清理队列中该 Agent 的条目（Agent 下线时调用，保留自定义标记以便重启后恢复）。
    /// </summary>
    public async Task ClearAsync(string agentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
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
    {
        var entries = new List<(WakeRequest, DateTime)>();
        bool found = false;
        while (_queue.TryDequeue(out var req, out var pri))
        {
            if (string.Equals(req.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                found = true;
            entries.Add((req, pri));
        }
        foreach (var (req, pri) in entries)
            _queue.Enqueue(req, pri);
        return found;
    }

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
            var entries = new List<(WakeRequest, DateTime)>();
            WakeRequest? found = null;
            while (_queue.TryDequeue(out var req, out var pri))
            {
                if (string.Equals(req.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                    found = req;
                entries.Add((req, pri));
            }
            foreach (var (req, pri) in entries)
                _queue.Enqueue(req, pri);
            return found;
        }
        finally { _gate.Release(); }
    }
}
