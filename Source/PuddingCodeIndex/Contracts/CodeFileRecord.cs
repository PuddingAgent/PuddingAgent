namespace PuddingCodeIndex.Contracts;

public sealed record CodeFileRecord(
    string WorkspaceId,
    string ProjectId,
    string FilePath,
    string? Language = null,
    DateTimeOffset? LastIndexedAtUtc = null);
