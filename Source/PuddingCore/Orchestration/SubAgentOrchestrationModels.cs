namespace PuddingCode.Orchestration;

/// <summary>Capabilities that an expert-group member can contribute to an orchestration plan.</summary>
[Flags]
public enum ExpertMemberCapabilities
{
    None = 0,
    ContextAudit = 1 << 0,
    Research = 1 << 1,
    Propose = 1 << 2,
    Critique = 1 << 3,
    Synthesize = 1 << 4,
    FinalReview = 1 << 5
}

/// <summary>Supported critique assignment strategies.</summary>
public enum CritiqueTopology
{
    /// <summary>Each proposal is reviewed by exactly two eligible members.</summary>
    DoubleReview,

    /// <summary>Every eligible member reviews every proposal except their own.</summary>
    AllToAll
}

/// <summary>Canonical phases in a design-council orchestration plan.</summary>
public enum SubAgentOrchestrationStageKind
{
    ContextAudit,
    Research,
    IndependentProposal,
    CrossCritique,
    Synthesis,
    FinalReview
}

/// <summary>Policy enforced before the next orchestration stage may start.</summary>
public enum SubAgentOrchestrationGateKind
{
    ContextResolved,
    EvidenceAvailable,
    ProposalQuorum,
    CritiqueCoverage,
    ChairSynthesis,
    IndependentFinalReview
}

/// <summary>Lifecycle state of a compiled orchestration plan.</summary>
public enum SubAgentOrchestrationPlanStatus
{
    /// <summary>The plan is inspectable but cannot dispatch work until explicitly activated.</summary>
    Draft,

    Active,
    AwaitingUserInput,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Which prior outputs a work item is allowed to observe.</summary>
public enum SubAgentWorkItemContextVisibility
{
    CanonicalRequestOnly,
    CanonicalRequestAndResearch,
    TargetProposalAndEvidence,
    AllPriorOutputs
}

/// <summary>
/// Canonical, immutable problem statement compiled before any expert sub-agent is dispatched.
/// Empty collections are allowed when the caller genuinely has no evidence, but acceptance criteria
/// and requested deliverables are required by the compiler.
/// </summary>
public sealed record DesignRequest
{
    public required string RequestId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ParentSessionId { get; init; }
    public required string RequestedByAgentId { get; init; }
    public required string UserIntent { get; init; }
    public required string ProblemStatement { get; init; }
    public IReadOnlyList<string> IntentEvidence { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Constraints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NonGoals { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KnownContext { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SuspectedContextGaps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ResearchQuestions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequestedDeliverables { get; init; } = Array.Empty<string>();
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One resolved expert member. RouteKey is the exact provider/model route chosen before compilation.</summary>
public sealed record ExpertGroupMemberDefinition
{
    public required string MemberId { get; init; }
    public required string Role { get; init; }
    public required string TemplateId { get; init; }
    public required string RouteKey { get; init; }
    public ExpertMemberCapabilities Capabilities { get; init; }
}

/// <summary>Model-independent policy for one expert group.</summary>
public sealed record ExpertGroupDefinition
{
    public required string GroupId { get; init; }
    public required string ChairMemberId { get; init; }
    public IReadOnlyList<ExpertGroupMemberDefinition> Members { get; init; } = Array.Empty<ExpertGroupMemberDefinition>();
    public int MinimumMembers { get; init; } = 3;
    public int MinimumSuccessfulProposals { get; init; } = 3;
    public int MinimumDistinctProposalRoutes { get; init; } = 3;
    public int MaxConcurrency { get; init; } = 4;
    public CritiqueTopology CritiqueTopology { get; init; } = CritiqueTopology.DoubleReview;

    /// <summary>
    /// MOA plans require exact routes. Enabling fallback would make model-diversity and quorum claims false.
    /// </summary>
    public bool AllowFallback { get; init; }
}

/// <summary>Input to the pure design-council plan compiler.</summary>
public sealed record DesignCouncilPlanCompileRequest
{
    public required string PlanId { get; init; }
    public required DesignRequest DesignRequest { get; init; }
    public required ExpertGroupDefinition ExpertGroup { get; init; }
}

/// <summary>Stable, machine-readable validation issue returned by plan compilation.</summary>
public sealed record SubAgentOrchestrationValidationIssue(
    string Code,
    string Message,
    string? Path = null);

/// <summary>One gated stage of a compiled sub-agent orchestration plan.</summary>
public sealed record SubAgentOrchestrationStage
{
    public required string StageId { get; init; }
    public required SubAgentOrchestrationStageKind Kind { get; init; }
    public required SubAgentOrchestrationGateKind Gate { get; init; }
    public int Order { get; init; }
    public IReadOnlyList<string> DependsOnStageIds { get; init; } = Array.Empty<string>();
    public int RequiredSuccessfulItems { get; init; }
    public int RequiredDistinctRoutes { get; init; }
    public int RequiredCritiquesPerProposal { get; init; }
    public bool PauseOnCriticalContextGap { get; init; }
}

/// <summary>
/// A declarative child-agent assignment. The compiler produces assignments but never dispatches them.
/// </summary>
public sealed record SubAgentOrchestrationWorkItem
{
    public required string WorkItemId { get; init; }
    public required string StageId { get; init; }
    public required SubAgentOrchestrationStageKind Kind { get; init; }
    public required string MemberId { get; init; }
    public required string Role { get; init; }
    public required string TemplateId { get; init; }
    public required string RouteKey { get; init; }
    public required string Instruction { get; init; }
    public required string ExpectedOutputContract { get; init; }
    public SubAgentWorkItemContextVisibility ContextVisibility { get; init; }
    public string? TargetWorkItemId { get; init; }

    /// <summary>Design and review assignments are read-only until the synthesized plan is approved.</summary>
    public bool IsReadOnly { get; init; } = true;
}

/// <summary>
/// Immutable output of plan compilation. RequiresExplicitActivation prevents a caller from treating
/// compilation as authorization to start child agents.
/// </summary>
public sealed record SubAgentOrchestrationPlan
{
    public required string PlanId { get; init; }
    public required DesignRequest DesignRequest { get; init; }
    public required string ExpertGroupId { get; init; }
    public SubAgentOrchestrationPlanStatus Status { get; init; } = SubAgentOrchestrationPlanStatus.Draft;
    public bool RequiresExplicitActivation { get; init; } = true;
    public bool AllowFallback { get; init; }
    public int MaxConcurrency { get; init; }
    public IReadOnlyList<SubAgentOrchestrationStage> Stages { get; init; } = Array.Empty<SubAgentOrchestrationStage>();
    public IReadOnlyList<SubAgentOrchestrationWorkItem> WorkItems { get; init; } = Array.Empty<SubAgentOrchestrationWorkItem>();
    public DateTimeOffset CompiledAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Compilation result; validation failures never produce a partially executable plan.</summary>
public sealed record DesignCouncilPlanCompilationResult
{
    public required IReadOnlyList<SubAgentOrchestrationValidationIssue> Issues { get; init; }
    public SubAgentOrchestrationPlan? Plan { get; init; }
    public bool Success => Plan is not null && Issues.Count == 0;
}
