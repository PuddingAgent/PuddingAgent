using PuddingCode.Models;
using PuddingCode.Runtime;

namespace PuddingCode.Abstractions;

/// <summary>
/// Records token usage events for billing and analytics.
/// </summary>
public interface ITokenUsageRecorder
{
    /// <summary>
    /// Best-effort usage recording for non-authoritative telemetry callers.
    /// Implementations may log and suppress persistence failures.
    /// </summary>
    Task RecordAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null,
        string? parentSessionId = null);

    /// <summary>
    /// Required usage-fact recording. Persistence failures must propagate so
    /// the owning workflow cannot report success while silently losing billing facts.
    /// </summary>
    Task RecordRequiredAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null,
        string? parentSessionId = null);

    /// <summary>
    /// Required usage recording with canonical Agent-loop attribution. The default body keeps
    /// older recorders source-compatible while implementations that own the attribution ledger
    /// can persist round/tool/sub-agent facts.
    /// </summary>
    Task RecordAttributedRequiredAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        TokenUsageAttribution attribution,
        PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null)
        => RecordRequiredAsync(
            usage,
            sourceType,
            sourceId,
            workspaceId,
            sessionId,
            providerId,
            modelId,
            prefixSnapshot,
            occurredAtUtc,
            attribution.ParentSessionId);
}

/// <summary>
/// Canonical per-LLM-call attribution supplied by the Agent Loop. Identity comes from
/// RuntimeExecutionIdentity; implementations must not infer sub-agent ownership from SessionId.
/// </summary>
public sealed record TokenUsageAttribution
{
    public string? ParentSessionId { get; init; }
    public string? SubAgentId { get; init; }
    public int? TurnRound { get; init; }
    public int? ToolCallCount { get; init; }
    public IReadOnlyList<string> ToolNames { get; init; } = [];
}
