namespace PuddingCode.Abstractions;

/// <summary>Read-only access to committed compaction coverage; never changes live history.</summary>
public interface ICompactionEvidenceReader
{
    Task<CompactionEvidencePage> ReadSourcesAsync(CompactionEvidenceQuery query, CancellationToken ct = default);
    Task<CompactionSourceWindow> ReadSourceAsync(CompactionEvidenceQuery query, string sourceMessageId,
        int contentOffset = 0, int windowSize = 16384, CancellationToken ct = default);
}

public sealed record CompactionEvidenceQuery(string WorkspaceId, string SessionId, string CompactionId,
    string? AgentInstanceId = null, int Offset = 0, int Limit = 20);

public sealed record CompactionSourceReference(string SourceMessageId, string SessionId, string? Role,
    string? TurnId, string? ChildCompactionId, string Availability);

public sealed record CompactionEvidencePage(string Status, IReadOnlyList<CompactionSourceReference> Sources,
    int TotalSources = 0, int? NextOffset = null);

public sealed record CompactionSourceWindow(string Status, string? Content = null, int TotalChars = 0,
    int? NextOffset = null, string? ContentType = null);
