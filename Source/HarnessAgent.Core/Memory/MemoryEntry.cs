namespace HarnessAgent.Core.Memory;

/// <summary>Memory content type.</summary>
public enum MemoryKind { Fact, Preference, Summary, Chapter, Pointer }

/// <summary>Reference type for source tracing.</summary>
public enum ReferenceKind { Internal, External, None }

/// <summary>
/// A single memory entry — the atomic unit of the 6-layer memory system.
/// </summary>
public sealed record MemoryEntry
{
    /// <summary>Stable identifier.</summary>
    public required string EntryId { get; init; }

    /// <summary>Content type.</summary>
    public required MemoryKind Kind { get; init; }

    /// <summary>Short title or label.</summary>
    public string Title { get; init; } = "";

    /// <summary>Main content text.</summary>
    public required string Content { get; init; }

    /// <summary>RFC 3339 creation timestamp.</summary>
    public string CreatedAt { get; init; } = "";

    /// <summary>RFC 3339 last modification timestamp.</summary>
    public string UpdatedAt { get; init; } = "";

    /// <summary>Source reference (session id, URL, etc.).</summary>
    public string? SourceReference { get; init; }

    /// <summary>Reference type.</summary>
    public ReferenceKind ReferenceType { get; init; } = ReferenceKind.None;

    /// <summary>Tags for categorization.</summary>
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();

    /// <summary>Optional scene context — when/where this memory applies.</summary>
    public string? Scene { get; init; }

    /// <summary>Optional constraints on usage.</summary>
    public string? Constraints { get; init; }

    /// <summary>Priority score (higher = more important).</summary>
    public int Priority { get; init; }

    /// <summary>Whether this entry is archived (hidden from normal queries).</summary>
    public bool IsArchived { get; init; }

    /// <summary>Expiry timestamp (RFC 3339), or null for permanent.</summary>
    public string? ExpiresAt { get; init; }

    /// <summary>Estimated token count of Content.</summary>
    public int EstimatedTokens => Content.Length / 4;
}

/// <summary>A named collection of related memory entries (a "book").</summary>
public sealed record MemoryBook
{
    public required string BookId { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();
    public string CreatedAt { get; init; } = "";
}

/// <summary>A relation between two entries.</summary>
public sealed record MemoryRelation
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required string RelationType { get; init; } // "parent", "related", "contradicts", etc.
    public string Description { get; init; } = "";
    public double Weight { get; init; } = 1.0;
}
