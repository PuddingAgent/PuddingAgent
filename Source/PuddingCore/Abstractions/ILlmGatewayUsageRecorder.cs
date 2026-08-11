using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Persists the provider-returned usage for exactly one gateway request.
/// This is the billing-oriented ledger boundary; higher-level conversation
/// projections may record additional attribution facts but must not replace it.
/// </summary>
public interface ILlmGatewayUsageRecorder
{
    Task RecordRequiredAsync(
        TokenUsageDto usage,
        string sourceId,
        string operation,
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        string providerId,
        string modelId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct = default);
}
