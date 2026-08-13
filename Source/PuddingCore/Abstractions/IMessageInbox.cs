using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>Pull-based inbox view over durable message deliveries.</summary>
public interface IMessageInbox
{
    Task<IReadOnlyList<MessageInboxItem>> ListAsync(MessageInboxQuery query, CancellationToken ct = default);

    /// <summary>
    /// Lists distinct durable delivery targets that still have queued or retrying work.
    /// The dispatcher uses this database-backed projection to recover work after a
    /// process restart; in-memory wakeup subscriptions are only a latency optimization.
    /// </summary>
    Task<IReadOnlyList<MessageDeliveryTarget>> ListPendingTargetsAsync(
        string targetKind,
        CancellationToken ct = default);

    Task<MessageInboxItem?> ClaimNextAsync(MessageClaimRequest request, CancellationToken ct = default);

    /// <summary>
    /// 批量声明同一目标的多条排队消息（最多 maxBatch 条）。
    /// 用于合并同一发送方→接收方的多条消息，减少队列积压和 Agent 执行次数。
    /// </summary>
    Task<IReadOnlyList<MessageInboxItem>> ClaimBatchAsync(
        MessageClaimRequest request,
        int maxBatch,
        CancellationToken ct = default);

    /// <summary>
    /// Renews an active delivery lease only while <paramref name="executionId"/>
    /// still owns the delivery. Returns false after expiry, recovery, or ownership
    /// transfer so a stale runtime execution can stop before producing side effects.
    /// </summary>
    Task<bool> RenewLeaseAsync(
        string deliveryId,
        string executionId,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default);

    Task AckAsync(string deliveryId, CancellationToken ct = default);

    Task AckAsync(string deliveryId, string executionId, CancellationToken ct = default);

    Task RetryAsync(string deliveryId, string executionId, string error, DateTimeOffset availableAt, CancellationToken ct = default);

    /// <summary>
    /// Defers a delivery because its target agent is busy — queueing, not failure
    /// backoff. Semantically identical to <see cref="RetryAsync"/> with
    /// availableAt = now (sets Retrying, keeps the delivery immediately claimable,
    /// clears the lease/claim, records lastError) and, like RetryAsync, never
    /// increments AttemptCount (the increment only happens at claim time).
    /// It is a separate method so busy deferral stays distinguishable from failure
    /// retry at call sites and in logs, and so the UI can surface it as
    /// "queued waiting for the agent to free up" instead of "retrying".
    /// </summary>
    Task DeferAsync(string deliveryId, string executionId, string error, CancellationToken ct = default);

    Task DeadLetterAsync(string deliveryId, string executionId, string error, CancellationToken ct = default);
}
