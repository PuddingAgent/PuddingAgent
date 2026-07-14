using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话事件流——自动合并历史重放 + 实时 live 订阅 + 去重 + 补洞。
/// <para>
/// 正确的 SSE 重连算法（ADR-056 §4.2）：
/// 1. 获取数据库高水位 H = GetHeadAsync
/// 2. 循环 ReadAfterAsync(afterExclusive, H]，不能只读一页
/// 3. 订阅 ISessionHeadNotifier 开始消费 live 通知
/// 4. 每次收到 committedThrough=N，ReadAfterAsync(lastSent, N]
/// 5. 跳过 sequence <= lastSent（内生去重，不依赖客户端）
/// 6. 检测跳号时暂停后续发送，先从 DB 补齐
/// <para>
/// 调用方（SessionEventsController SSE 端点）只负责将 SessionEventEnvelope 序列化为 SSE，
/// 不处理重放、去重、补洞逻辑。
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
