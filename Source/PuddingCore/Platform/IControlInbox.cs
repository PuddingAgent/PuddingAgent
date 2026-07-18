namespace PuddingCode.Platform;

/// <summary>
/// ADR-059: 持久化控制收件箱 — Read-then-Ack 协议。
/// Read 不改变状态；应用成功后才 Ack。崩溃恢复后未 Ack 的消息可重新投递。
/// </summary>
public interface IControlInbox
{
    /// <summary>写入控制消息。调用方负责同事务写 Conversation Event。</summary>
    Task<ControlMessageRecord> EnqueueAsync(
        string conversationId, string? turnId, ControlMessageKind kind,
        string payload, string? sourceUserId, int priority, CancellationToken ct);

    /// <summary>读取 pending 消息（不修改状态）。</summary>
    Task<IReadOnlyList<ControlMessageRecord>> ReadPendingAsync(
        ExecutionLease lease, long afterSequence, CancellationToken ct);

    /// <summary>确认消息已处理（应用成功后调用）。必须验证 runId+fencingToken。</summary>
    Task AcknowledgeAsync(
        ExecutionLease lease, string controlId, CancellationToken ct);
}

/// <summary>
/// 控制消息持久记录 — Inbox 返回给 Runtime 的读模型。
/// </summary>
public sealed record ControlMessageRecord(
    string ControlId,
    long Sequence,
    string ConversationId,
    string? TurnId,
    ControlMessageKind Kind,
    string Payload,
    string? SourceUserId,
    int Priority,
    string Status,
    DateTimeOffset CreatedAt);
