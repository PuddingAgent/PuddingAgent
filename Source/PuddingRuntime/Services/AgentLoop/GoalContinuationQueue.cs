namespace PuddingRuntime.Services.AgentLoop;

/// <summary>一条待消费的续跑请求。</summary>
public sealed record GoalContinuationRequest(string SessionId, int Round, string ReasonCode, DateTimeOffset EnqueuedAt);

/// <summary>
/// 续跑请求队列：把「决策说继续」变成**可被宿主消费的一条请求**。
/// <para>
/// 为什么不直接在钩子里再跑一轮：续跑意味着重新进入执行循环，那属于宿主的职责边界。
/// 钩子擅自重入会让「谁在驱动这个会话」变得含糊 —— 出事时无法归因。
/// 所以这里只做**生产者**：决策通过 → 入队；由宿主在合适的时机消费。
/// </para>
/// <para>
/// 会话维度去重：同一会话最多一条待消费请求。重复入队只更新轮号与原因，
/// 避免"决策反复说继续"把队列堆爆。
/// </para>
/// </summary>
public sealed class GoalContinuationQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GoalContinuationRequest> _pending = new(StringComparer.Ordinal);

    /// <summary>入队（或覆盖）一条续跑请求。</summary>
    public GoalContinuationRequest Enqueue(string sessionId, int round, string reasonCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        var request = new GoalContinuationRequest(sessionId, round, reasonCode, now);
        lock (_gate)
            _pending[sessionId] = request;
        return request;
    }

    /// <summary>取出并移除该会话的请求；没有则 null。</summary>
    public GoalContinuationRequest? TryTake(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        lock (_gate)
        {
            if (!_pending.TryGetValue(sessionId, out var request))
                return null;

            _pending.Remove(sessionId);
            return request;
        }
    }

    /// <summary>丢弃该会话的请求（目标被暂停/完成、或人类插话时调用）。</summary>
    public bool Clear(string sessionId)
    {
        lock (_gate)
            return _pending.Remove(sessionId);
    }

    /// <summary>当前待消费数量（诊断用）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>当前待消费快照（诊断用）。</summary>
    public IReadOnlyCollection<GoalContinuationRequest> Snapshot()
    {
        lock (_gate)
            return _pending.Values.ToArray();
    }
}
