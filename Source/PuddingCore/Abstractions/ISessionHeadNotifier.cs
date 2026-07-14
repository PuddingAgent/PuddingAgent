using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话 Head 通知器——只通知 committed sequence 水位，不携带 Payload。
/// <para>
/// ADR-056 核心架构原则：
/// 1. Channel 是通知 "N 已提交到 session_event_log"，不是事件传输层。
/// 2. SSE 收到 SessionHeadAdvanced 后从 ISessionEventReader 读取实际事件。
/// 3. 通知丢失不会导致数据丢失——下一个通知携带更大的 N，客户端自然补齐。
/// 4. BoundedChannelFullMode.DropOldest 在此模式下是安全的（最多丢失一次唤醒）。
/// </para>
/// </summary>
public interface ISessionHeadNotifier
{
    /// <summary>
    /// 订阅某会话的 Head 推进通知。
    /// 返回的 IAsyncEnumerable 在会话关闭/取消时完成。
    /// 同一会话可被多个订阅者共享（fan-out）。
    /// </summary>
    IAsyncEnumerable<SessionHeadAdvanced> SubscribeAsync(
        string sessionId,
        CancellationToken ct = default);
}
