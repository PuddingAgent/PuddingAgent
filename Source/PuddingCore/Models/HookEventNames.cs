using PuddingCode.Abstractions;

namespace PuddingCode.Models;

/// <summary>Canonical Hook v2 event names.</summary>
public static class HookEventNames
{
    public static readonly HookEventName SessionCompressed = new("session.compressed");
    public static readonly HookEventName SessionCompactionFailed = new("session.compaction_failed");
    public static readonly HookEventName AgentLoopCompleted = new("agent.loop.completed");
}

/// <summary>Payload emitted after a session compaction completes successfully.</summary>
public sealed record SessionCompressedHookPayload
{
    public required string WorkspaceId { get; init; }
    public required string OriginalSessionId { get; init; }
    public string? NewSessionId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public required string CompactionId { get; init; }
    public required string Mode { get; init; }
    public required string Level { get; init; }
    public required string Reason { get; init; }
    public int? OriginalMessageCount { get; init; }
    public int? PreservedMessageCount { get; init; }
    public int? DroppedMessageCount { get; init; }
    public string? SummaryPreview { get; init; }
    public IReadOnlyList<string> MemoryNotes { get; init; } = [];
    public DateTimeOffset CompressedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
