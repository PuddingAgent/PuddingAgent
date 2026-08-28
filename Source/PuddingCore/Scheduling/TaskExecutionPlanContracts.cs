namespace PuddingCode.Scheduling;

/// <summary>Deterministic execution phases emitted from a structured WorkspaceTask.</summary>
public enum TaskWorkUnitKind
{
    Explore = 0,
    Plan = 1,
    Change = 2,
    Test = 3,
    Review = 4,
}

/// <summary>External fact families that may suspend a WorkUnit without an LLM polling round.</summary>
public enum TaskWorkUnitAwaitKind
{
    TerminalJob = 0,
    SubAgent = 1,
    Approval = 2,
    External = 3,
}

/// <summary>Durable AwaitHandle lifecycle. Only a fenced consumer may mark Consumed.</summary>
public enum TaskWorkUnitAwaitStatus
{
    Waiting = 0,
    Signaled = 1,
    Consumed = 2,
    Cancelled = 3,
}

public sealed record TaskWorkUnitAwaitHandleSnapshot
{
    public required string AwaitHandleId { get; init; }
    public required string PlanId { get; init; }
    public required string WorkUnitId { get; init; }
    public required TaskWorkUnitAwaitKind Kind { get; init; }
    public string? ExternalId { get; init; }
    public required TaskWorkUnitAwaitStatus Status { get; init; }
    public required long FencingToken { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? SignaledAtUtc { get; init; }
    public DateTimeOffset? ConsumedAtUtc { get; init; }
}

/// <summary>Frozen per-WorkUnit guardrails. Runtime enforcement is a separate gate.</summary>
public sealed record TaskWorkUnitBudget
{
    public required int MaxRounds { get; init; }
    public required int MaxToolCalls { get; init; }
    public required int MaxDurationSeconds { get; init; }
    public required long MaxInputTokens { get; init; }
    public required long MaxOutputTokens { get; init; }
    public required decimal MaxCost { get; init; }
}

/// <summary>One immutable execution-plan node.</summary>
public sealed record TaskWorkUnitSnapshot
{
    public required string WorkUnitId { get; init; }
    public required int Sequence { get; init; }
    public required TaskWorkUnitKind Kind { get; init; }
    public required string Objective { get; init; }
    public required IReadOnlyList<string> DependsOn { get; init; }
    public required IReadOnlyList<string> RequiredCapabilityIds { get; init; }
    public required IReadOnlyList<string> ConflictScopes { get; init; }
    public required TaskWorkUnitBudget Budget { get; init; }
    public required string RetryPolicy { get; init; }
}

/// <summary>
/// Versioned, content-addressed execution snapshot. The fingerprint is recomputed
/// inside the Task-bound Goal transaction before any authoritative mutation.
/// </summary>
public sealed record TaskExecutionPlanSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required int PlanVersion { get; init; }
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required int TaskVersion { get; init; }
    public required string TaskType { get; init; }
    public required string PlanKind { get; init; }
    public required IReadOnlyList<TaskWorkUnitSnapshot> WorkUnits { get; init; }
    public required string Fingerprint { get; init; }
}
