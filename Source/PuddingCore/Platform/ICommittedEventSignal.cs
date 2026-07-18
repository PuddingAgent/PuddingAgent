namespace PuddingCode.Platform;

/// <summary>
/// ADR-057-C: Committed Event Signal 接口。
/// 只表示数据库可能存在新事件，不携带业务数据。
/// 调用方必须读取 Event Store 确认实际 Head。
/// </summary>
public interface ICommittedEventSignal
{
    /// <summary>
    /// 等待 conversation 的新 committed head 通知。
    /// 可以在多个等待者之间广播。
    /// </summary>
    /// <param name="conversationId">目标 conversation。</param>
    /// <param name="knownHead">当前已知的 head sequence。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask WaitForChangeAsync(
        string conversationId,
        long knownHead,
        CancellationToken ct);

    /// <summary>
    /// 发布 committed head 信号。
    /// </summary>
    void Signal(string conversationId, long committedThroughSequence);
}
