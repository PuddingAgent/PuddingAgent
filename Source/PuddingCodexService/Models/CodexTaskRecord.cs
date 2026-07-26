namespace PuddingCodexService.Models;

public enum CodexTaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record CodexTaskRecord
{
    public required string TaskId { get; init; }
    public string? ParentTaskId { get; init; }
    public string? ThreadId { get; init; }
    public required string Prompt { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? Model { get; init; }
    public required string Sandbox { get; init; }
    public required string ApprovalPolicy { get; init; }
    public required CodexTaskStatus Status { get; init; }
    public string? StatusMessage { get; init; }
    public string? ResultJson { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public long Revision { get; init; }
}

public sealed record CodexExecutionResult(
    string? ThreadId,
    string ResultJson,
    bool IsError);

public sealed record CodexTaskAccepted(
    string TaskId,
    CodexTaskStatus Status,
    DateTimeOffset CreatedAtUtc);

public sealed record PuddingRestartAccepted(
    string RequestId,
    string TaskId,
    DateTimeOffset NotBeforeUtc);
