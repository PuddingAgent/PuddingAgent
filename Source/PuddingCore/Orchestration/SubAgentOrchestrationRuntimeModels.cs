namespace PuddingCode.Orchestration;

/// <summary>Runtime lifecycle of one compiled orchestration work item.</summary>
public enum SubAgentOrchestrationWorkItemStatus
{
    Pending,
    Ready,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>Runtime lifecycle of one orchestration stage.</summary>
public enum SubAgentOrchestrationStageStatus
{
    Pending,
    Ready,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Terminal result reported by an executing child-agent adapter.</summary>
public enum SubAgentWorkItemOutcome
{
    Succeeded,
    Failed
}

/// <summary>One accepted user response that resolves questions raised by expert members.</summary>
public sealed record SubAgentOrchestrationContextResolution
{
    public required string ResolutionId { get; init; }
    public required string ProvidedBy { get; init; }
    public required string Response { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>Completion submitted for a currently claimed work item.</summary>
public sealed record SubAgentWorkItemCompletion
{
    public required string WorkItemId { get; init; }
    public required string ClaimId { get; init; }
    public SubAgentWorkItemOutcome Outcome { get; init; }
    public string? Summary { get; init; }
    /// <summary>Full member output retained for later visibility-scoped stages.</summary>
    public string? OutputText { get; init; }
    public string? OutputReference { get; init; }
    public string? ExternalRunId { get; init; }
    public string? ExternalSubSessionId { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> ContextGaps { get; init; } = Array.Empty<string>();
    public bool RequiresUserInput { get; init; }
    public IReadOnlyList<string> BlockingQuestions { get; init; } = Array.Empty<string>();
}

/// <summary>Mutable lifecycle data represented as an immutable snapshot entry.</summary>
public sealed record SubAgentOrchestrationWorkItemState
{
    public required string WorkItemId { get; init; }
    public SubAgentOrchestrationWorkItemStatus Status { get; init; } = SubAgentOrchestrationWorkItemStatus.Pending;
    public int Attempt { get; init; }
    public string? ClaimId { get; init; }
    public string? ExternalRunId { get; init; }
    public string? ExternalSubSessionId { get; init; }
    public DateTimeOffset? ClaimedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? Summary { get; init; }
    public string? OutputText { get; init; }
    public string? OutputReference { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> ContextGaps { get; init; } = Array.Empty<string>();
    public bool RequiresUserInput { get; init; }
    public IReadOnlyList<string> BlockingQuestions { get; init; } = Array.Empty<string>();
}

/// <summary>Mutable lifecycle data represented as an immutable stage snapshot entry.</summary>
public sealed record SubAgentOrchestrationStageState
{
    public required string StageId { get; init; }
    public SubAgentOrchestrationStageStatus Status { get; init; } = SubAgentOrchestrationStageStatus.Pending;
    public DateTimeOffset? OpenedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
}

/// <summary>Durable shape consumed by a future plan store and runtime adapter.</summary>
public sealed record SubAgentOrchestrationRunSnapshot
{
    public required string RunId { get; init; }
    public required SubAgentOrchestrationPlan Plan { get; init; }
    public SubAgentOrchestrationPlanStatus Status { get; init; } = SubAgentOrchestrationPlanStatus.Draft;
    public string? CurrentStageId { get; init; }
    public IReadOnlyList<SubAgentOrchestrationStageState> Stages { get; init; } = Array.Empty<SubAgentOrchestrationStageState>();
    public IReadOnlyList<SubAgentOrchestrationWorkItemState> WorkItems { get; init; } = Array.Empty<SubAgentOrchestrationWorkItemState>();
    public IReadOnlyList<SubAgentOrchestrationContextResolution> ContextResolutions { get; init; } = Array.Empty<SubAgentOrchestrationContextResolution>();
    public IReadOnlyList<string> BlockingQuestions { get; init; } = Array.Empty<string>();
    public string? PauseReason { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public long Version { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? ActivatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
}

/// <summary>A claimed assignment that a future runtime adapter may dispatch.</summary>
public sealed record SubAgentOrchestrationClaimedWorkItem
{
    public required string ClaimId { get; init; }
    public required SubAgentOrchestrationWorkItem WorkItem { get; init; }
}

/// <summary>Stable machine-readable issue for rejected state-machine operations.</summary>
public sealed record SubAgentOrchestrationOperationIssue(string Code, string Message);

/// <summary>Result of activation, completion, resume, or cancellation.</summary>
public sealed record SubAgentOrchestrationTransitionResult
{
    public required SubAgentOrchestrationRunSnapshot Snapshot { get; init; }
    public required IReadOnlyList<SubAgentOrchestrationOperationIssue> Issues { get; init; }
    public bool Success => Issues.Count == 0;
}

/// <summary>Result of atomically claiming ready work within the plan concurrency limit.</summary>
public sealed record SubAgentOrchestrationClaimResult
{
    public required SubAgentOrchestrationRunSnapshot Snapshot { get; init; }
    public required IReadOnlyList<SubAgentOrchestrationClaimedWorkItem> ClaimedWorkItems { get; init; }
    public required IReadOnlyList<SubAgentOrchestrationOperationIssue> Issues { get; init; }
    public bool Success => Issues.Count == 0;
}
