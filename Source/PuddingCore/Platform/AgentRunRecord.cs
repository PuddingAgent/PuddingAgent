namespace PuddingCode.Platform;

/// <summary>Agent work lifecycle record used by Agent-first chat clients.</summary>
public sealed record AgentRunRecord
{
    public required string RunId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string OwnerUserId { get; init; }
    public required string AgentId { get; init; }
    public required string MainSessionId { get; init; }
    public string? CommandClientId { get; init; }
    public string? InputMessageId { get; init; }
    public string? OutputMessageId { get; init; }
    public required string Status { get; init; }
    public string StatusText { get; init; } = "";
    public string Summary { get; init; } = "";
    public long EventCursor { get; init; }
    public string OutputSnapshotJson { get; init; } = "{}";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
