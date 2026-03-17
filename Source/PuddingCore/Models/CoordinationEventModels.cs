namespace PuddingCode.Models;

/// <summary>协同总线事件类型。</summary>
public enum CoordinationEventKind
{
    LockAcquired,
    LockDenied,
    LockReleased,
    LockForceReleased,
    LockExpired,
    UnlockRequested
}

/// <summary>协同总线事件记录。</summary>
public sealed record CoordinationEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public CoordinationEventKind Kind { get; init; }
    /// <summary>关联的锁 ID（UnlockRequested 时为被请求释放的锁 ID）。</summary>
    public string? LockId { get; init; }
    /// <summary>触发事件的 Agent ID。</summary>
    public string AgentId { get; init; } = "";
    /// <summary>补充说明（拒绝原因、释放理由等）。</summary>
    public string? Detail { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 协同总线订阅句柄——用于取消订阅。
/// </summary>
public sealed class CoordinationEventSubscription : IDisposable
{
    private readonly Action _unsubscribe;
    public CoordinationEventSubscription(Action unsubscribe) => _unsubscribe = unsubscribe;
    public void Dispose() => _unsubscribe();
}
