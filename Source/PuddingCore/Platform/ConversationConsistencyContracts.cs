namespace PuddingCode.Platform;

/// <summary>
/// P0-4f: Conversation 事件一致性报告契约（契约冻结，暂不切流）。
/// 由一致性检查实现（4f-3）填充，本类型仅定义字段形状。
/// </summary>
public sealed record ConversationConsistencyReport
{
    public required string ConversationId { get; init; }
    public required long CanonicalHead { get; init; }
    public long MinAvailableSequence { get; init; }
    public long SequenceGapCount { get; init; }
    public long DuplicateEventCount { get; init; }
    public required IReadOnlyList<ProjectorCheckpointEntry> Projectors { get; init; }
    public required ArchiveCheckpointEntry Archive { get; init; }
}

public sealed record ProjectorCheckpointEntry
{
    public required string ProjectorName { get; init; }
    public long Checkpoint { get; init; }
    public long Lag { get; init; } // = canonicalHead - checkpoint
    public required string Status { get; init; } // caught_up | lagging | invalid
}

public sealed record ArchiveCheckpointEntry
{
    public long Checkpoint { get; init; }
    public long Lag { get; init; }
    public DateTimeOffset? LastArchivedAt { get; init; }
}
