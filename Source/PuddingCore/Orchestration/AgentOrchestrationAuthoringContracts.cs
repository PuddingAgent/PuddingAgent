namespace PuddingCode.Orchestration;

/// <summary>
/// No-side-effect draft validation command for authoring clients. The compiler runs against the
/// draft exactly as it would run before a revision save; nothing is persisted.
/// </summary>
public sealed record AgentOrchestrationDraftValidateRequest
{
    public required string GraphId { get; init; }

    /// <summary>Optional base revision the editor believes it is editing; used for stale-draft hints.</summary>
    public string? BaseRevisionId { get; init; }

    public required AgentOrchestrationGraphDefinition Definition { get; init; }
}

/// <summary>Result of a side-effect-free draft validation pass.</summary>
public sealed record AgentOrchestrationDraftValidationResult
{
    public required bool IsValid { get; init; }

    /// <summary>Normalized (snapshot) definition produced by the compiler; null when validation fails.</summary>
    public AgentOrchestrationGraphDefinition? NormalizedDefinition { get; init; }

    public IReadOnlyList<AgentOrchestrationValidationIssue> Issues { get; init; }
        = Array.Empty<AgentOrchestrationValidationIssue>();

    public IReadOnlyList<string> TopologicalNodeIds { get; init; }
        = Array.Empty<string>();
}

/// <summary>
/// Domain command for creating the next immutable revision on a graph head. Audit fields
/// (revision, revisionId, parentRevisionId, createdByAgentId, createdAtUtc) are always
/// server-authored and never trusted from the client definition.
/// </summary>
public sealed record AgentOrchestrationRevisionCreateRequest
{
    public required string GraphId { get; init; }

    /// <summary>Expected current head revision; the next revision is expectedCurrentRevision + 1.</summary>
    public int ExpectedCurrentRevision { get; init; }

    public required AgentOrchestrationGraphDefinition Definition { get; init; }
}
