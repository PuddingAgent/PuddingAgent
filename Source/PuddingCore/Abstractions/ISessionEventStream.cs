using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话事件流——合并 replay + live pub/sub。
/// <para>
/// Controller 调用 FollowAsync 自动处理：
/// 1. 从 afterExclusive 之后重放历史事件
/// 2. 订阅 Head 通知并追赶数据库
/// 3. 去重（跳过 sequence <= lastSent）
/// 4. 补洞（跳号时暂停，先从 DB 补齐）
/// </para>
/// </summary>
public interface ISessionEventStream
{
    /// <summary>
    /// 跟随会话事件流。
    /// 先发送 afterExclusive 之后的所有已提交事件，然后持续发送新事件。
    /// 在 ct 被取消或会话关闭时完成。
    /// </summary>
    IAsyncEnumerable<SessionEventEnvelope> FollowAsync(
        string sessionId,
        long afterExclusive,
        CancellationToken ct = default);
}
