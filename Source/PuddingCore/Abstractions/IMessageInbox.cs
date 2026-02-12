using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>Pull-based inbox view over durable message deliveries.</summary>
public interface IMessageInbox
{
    Task<IReadOnlyList<MessageInboxItem>> ListAsync(MessageInboxQuery query, CancellationToken ct = default);

    Task<MessageInboxItem?> ClaimNextAsync(MessageClaimRequest request, CancellationToken ct = default);

    /// <summary>
    /// 批量声明同一目标的多条排队消息（最多 maxBatch 条）。
    /// 用于合并同一发送方→接收方的多条消息，减少队列积压和 Agent 执行次数。
    /// </summary>
    Task<IReadOnlyList<MessageInboxItem>> ClaimBatchAsync(
        MessageClaimRequest request,
        int maxBatch,
        CancellationToken ct = default);

    Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default);

    Task AckAsync(string deliveryId, CancellationToken ct = default);

    Task AckAsync(string deliveryId, string executionId, CancellationToken ct = default);

    Task RetryAsync(string deliveryId, string executionId, string error, DateTimeOffset availableAt, CancellationToken ct = default);

    Task DeadLetterAsync(string deliveryId, string executionId, string error, CancellationToken ct = default);
}
