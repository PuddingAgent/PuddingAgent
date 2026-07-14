namespace PuddingCode.Platform;

/// <summary>
/// 会话事件投影游标存储。
/// <para>
/// ADR-056：SessionEventLog 是唯一事实源，ChatMessages 是其投影物化表。
/// projectedThroughSequence 记录"已投影到 ChatMessages 的最新 Sequence"，
/// 浏览器加载历史消息后只需从该游标之后读取尾部队列事件。
/// </para>
/// </summary>
public interface ISessionProjectionStore
{
    /// <summary>
    /// 获取会话已投影到 ChatMessages 的最新 Sequence（0 表示未投影任何事件）。
    /// </summary>
    Task<long> GetProjectedCursorAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 更新会话的投影游标（= 已经投影完成的最新 Sequence）。
    /// 幂等：只接受更大的 sequence。
    /// </summary>
    Task SetProjectedCursorAsync(string sessionId, long sequence, CancellationToken ct = default);
}
