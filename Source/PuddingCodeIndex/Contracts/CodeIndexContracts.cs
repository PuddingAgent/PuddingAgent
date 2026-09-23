namespace PuddingCodeIntelligence.Contracts;

public enum CodeIndexStatus
{
    Unknown,
    Pending,
    Running,
    Completed,
    Failed,
}

public sealed record CodeIndexResult(
    bool Success,
    CodeIndexStatus Status,
    string? Message = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? WorkspaceId = null,
    string? ProjectId = null);
