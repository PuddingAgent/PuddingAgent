namespace PuddingCode.Platform;

/// <summary>
/// ADR-057-B: Conversation Event Store 接口。
/// 提供 conversation 内 sequence 原子分配、事件追加与查询。
/// </summary>
public interface IConversationEventStore
{
    /// <summary>
    /// 原子追加一批事件到 Conversation Event Log。
    /// </summary>
    /// <param name="conversationId">所属 conversation。</param>
    /// <param name="expectedVersion">预期 conversation 版本（-1 表示不校验）。</param>
    /// <param name="events">待持久化的事件列表。</param>
    /// <param name="condition">写入条件（fencing、幂等等）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>追加结果，包含分配的 sequence 区间。</returns>
    Task<AppendResult> AppendAsync(
        string conversationId,
        long expectedVersion,
        IReadOnlyList<NewConversationEvent> events,
        EventWriteCondition condition,
        CancellationToken ct);

    /// <summary>
    /// 正向读取事件。afterExclusive 为 exclusive 语义。
    /// </summary>
    Task<EventPage> ReadForwardAsync(
        string conversationId,
        long afterExclusive,
        long? throughInclusive,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// 反向读取事件（用于历史加载）。
    /// </summary>
    Task<EventPage> ReadBackwardAsync(
        string conversationId,
        long beforeExclusive,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// 获取 conversation 的事件边界。
    /// </summary>
    Task<EventBounds> GetBoundsAsync(
        string conversationId,
        CancellationToken ct);

    /// <summary>
    /// 确保物理表存在（启动时调用）。
    /// </summary>
    Task EnsureTablesAsync(CancellationToken ct);
}
