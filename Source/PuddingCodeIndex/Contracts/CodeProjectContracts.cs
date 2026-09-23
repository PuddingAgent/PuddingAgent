namespace PuddingCodeIntelligence.Contracts;

public enum CodeProjectStatus
{
    Unknown,
    Registering,
    Active,
    Removing,
    Removed,
    Failed,
}

public sealed record CodeProjectRecord(
    string WorkspaceId,
    string ProjectId,
    string ProjectPath,
    CodeProjectStatus Status,
    string? DisplayName = null,
    DateTimeOffset? AddedAtUtc = null,
    DateTimeOffset? UpdatedAtUtc = null,
    ScopeSource? Source = null,
    ScopeState? ScopeState = null);

public sealed record CodeProjectAddRequest(
    string WorkspaceId,
    string ProjectPath,
    string? ProjectId = null,
    string? DisplayName = null);

public sealed record CodeProjectRemoveRequest(
    string WorkspaceId,
    string ProjectId,
    bool RemoveIndexData = true);
