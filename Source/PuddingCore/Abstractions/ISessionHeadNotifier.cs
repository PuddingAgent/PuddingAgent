using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话 Head 通知器——不携带 Payload，只通知 confirmed 的 sequence。
/// <para>
/// Channel 订阅者收到通知后，应从 ISessionEventReader 读取实际事件。
/// 通知丢失不会导致数据丢失，下一个更大的通知或高水位检查仍能补齐。
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
