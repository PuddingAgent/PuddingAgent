namespace PuddingCode.Abstractions;

/// <summary>
/// Request to save a compressed agent content summary after compaction.
/// </summary>
public sealed record AgentCompressedContentSummaryRequest(
    string WorkspaceId,
    string AgentInstanceId,
    string? AgentTemplateId,
    string SessionId,
    string Day,
    string SummaryMarkdown,
    string Reason);

/// <summary>
/// Result of saving a compressed agent content summary.
/// </summary>
public sealed record AgentContentSummaryResult(
    string AgentInstanceId,
    string Day,
    string ContentPath,
    string MetadataPath,
    string SourceHash,
    bool ResetForNewDay);

/// <summary>
/// Manages agent content summaries for compaction decisions. File-based, no EF Core dependency.
/// </summary>
public interface IAgentContentSummaryService
{
    Task<AgentContentSummaryResult> SaveCompressedSummaryAsync(
        AgentCompressedContentSummaryRequest request,
        CancellationToken ct = default);
}
