using PuddingCode.Models;
using PuddingCode.Runtime;

namespace PuddingCode.Abstractions;

/// <summary>
/// Records token usage events for billing and analytics.
/// </summary>
public interface ITokenUsageRecorder
{
    Task RecordAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        PromptPrefixSnapshot? prefixSnapshot = null,
        DateTimeOffset? occurredAtUtc = null);
}
