using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话事件写入器——所有事件持久化的唯一入口。
/// <para>
/// ADR-056：事件的权威来源是 SQLite Event Store (session_event_log)。
/// ISessionEventWriter 保证以下不变量：
/// 1. AppendAsync 返回前，事件已成功写入 SQLite（持久化优先）。
/// 2. 只有在 SQLite 提交成功后，才向 Channel 发送 head 通知。
/// 3. Channel 不承载事件 Payload——只通知 "sequence N 已提交"。
/// 4. 调用方必须 await AppendAsync，不可 fire-and-forget。
/// </para>
/// <para>
/// 实现：SessionStateManager 同时实现 ISessionEventWriter + ISessionEventReader +
/// ISessionHeadNotifier，构成可靠事件流的三个端口。
/// </para>
/// </summary>
public interface ISessionEventWriter
{
    /// <summary>
    /// 追加单个事件到 Event Store。返回已持久化的完整 Envelope。
    /// <para>
    /// 内部行为：
    /// 1. 在 per-session SemaphoreSlim 下分配递增 Sequence（ADR-028）
    /// 2. 在同一临界区内完成 Insert + SaveChangesAsync（序号与持久化原子）
    /// 3. 如果存在 Head 订阅者，发布 SessionHeadAdvanced 通知
    /// 4. 返回包含分配 Sequence 的 SessionEventEnvelope
    /// </para>
    /// </summary>
    ValueTask<SessionEventEnvelope> AppendAsync(
        string sessionId,
        string workspaceId,
        SessionEventDraft draft,
        CancellationToken ct = default);

    /// <summary>
    /// 批量追加事件，在同一 SQLite 事务内提交。
    /// 返回按写入顺序排列的已完成 Envelope 列表。
    /// </summary>
    ValueTask<IReadOnlyList<SessionEventEnvelope>> AppendBatchAsync(
        string sessionId,
        string workspaceId,
        IReadOnlyList<SessionEventDraft> drafts,
        CancellationToken ct = default);
}
