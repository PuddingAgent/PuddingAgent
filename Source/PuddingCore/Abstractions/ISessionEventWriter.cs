using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话事件写入器——先持久化后通知的唯一入口。
/// <para>
/// ADR-056: 所有 Append 方法内部保证 SQLite 提交后才返回/通知。
/// 调用方必须 await AppendAsync，不可 fire-and-forget。
/// </para>
/// </summary>
public interface ISessionEventWriter
{
    /// <summary>
    /// 追加单个事件。返回已持久化的完整 Envelope（含自动分配的 Sequence）。
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
