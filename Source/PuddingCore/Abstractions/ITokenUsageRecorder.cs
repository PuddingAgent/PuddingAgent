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
}
