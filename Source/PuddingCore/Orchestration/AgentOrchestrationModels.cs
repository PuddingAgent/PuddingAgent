using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingCode.Orchestration;

/// <summary>Stable schema identifiers for agent-authored orchestration definitions.</summary>
public static class AgentOrchestrationSchemas
{
    public const string GraphDefinitionV2 = "pudding.agent-orchestration/v2";
}

/// <summary>Supported execution semantics for a graph node.</summary>
public enum AgentOrchestrationNodeKind
{
    SubAgent,
    Tool,
    HumanInput,
    Gate
}

/// <summary>Runtime binding used to execute a node.</summary>
public enum AgentOrchestrationExecutorKind
{
    SubAgent,
    Tool
}

/// <summary>Whether an edge controls scheduling or exposes upstream output.</summary>
public enum AgentOrchestrationEdgeKind
{
    Control,
    Data
}

/// <summary>Terminal condition that satisfies an edge.</summary>
public enum AgentOrchestrationEdgeCondition
{
    OnSuccess,
    OnCompletion,
    Always
}

/// <summary>How multiple upstream values are applied to the same logical node input.</summary>
public enum AgentOrchestrationDataAggregation
{
    Replace,
    Append
}

/// <summary>Permission boundary requested by a node definition.</summary>
public enum AgentOrchestrationPermissionMode
{
    ReadOnly,
    ExplicitWrite
}

/// <summary>Policy applied when a node exhausts its attempts.</summary>
public enum AgentOrchestrationFailureBehavior
{
    FailRun,
    Continue,
    AwaitDecision
}

/// <summary>Lifecycle state projected for an orchestration run.</summary>
public enum AgentOrchestrationRunStatus
{
    Draft,
    Active,
    AwaitingInput,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Lifecycle state projected for one node execution.</summary>
public enum AgentOrchestrationNodeRunStatus
{
    Pending,
    Ready,
    Claimed,
    Running,
    AwaitingInput,
    Completed,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>One typed graph input and its optional immutable default value.</summary>
public sealed record AgentOrchestrationGraphInput
{
    public required string InputId { get; init; }
    public required AgentOrchestrationDataContract Contract { get; init; }
    public AgentOrchestrationValueEnvelope? DefaultValue { get; init; }
    public bool RequiredAtActivation { get; init; } = true;
}

/// <summary>Maps a graph-level input to a typed component input port.</summary>
public sealed record AgentOrchestrationGraphInputBinding
{
    public required string InputId { get; init; }
    public required string TargetPortId { get; init; }
    public string? TargetKey { get; init; }
}

/// <summary>
/// Declarative executor selection. An activatable sub-agent node must freeze RouteKey to an exact
/// provider/model route; role and template remain audit metadata rather than fallback selectors.
/// </summary>
public sealed record AgentOrchestrationExecutorBinding
{
    public AgentOrchestrationExecutorKind Kind { get; init; }
    public string? Role { get; init; }
    public string? TemplateId { get; init; }
    public string? RouteKey { get; init; }
    public string? ToolId { get; init; }
}

/// <summary>Runtime-owned gate evaluator and its versioned, string-valued parameters.</summary>
public sealed record AgentOrchestrationGateDefinition
{
    public required string EvaluatorId { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>One data mapping carried by a data edge.</summary>
public sealed record AgentOrchestrationDataBinding
{
    public required string SourcePortId { get; init; }
    public string SourcePath { get; init; } = "$";
    public required string TargetPortId { get; init; }
    public string? TargetKey { get; init; }
    public AgentOrchestrationDataAggregation Aggregation { get; init; }
        = AgentOrchestrationDataAggregation.Append;
}

/// <summary>One typed node in an agent-authored orchestration graph.</summary>
public sealed record AgentOrchestrationNodeDefinition
{
    public required string NodeId { get; init; }
    public required AgentOrchestrationNodeKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Objective { get; init; }
    public required AgentOrchestrationComponentReference Component { get; init; }
    public AgentOrchestrationExecutorBinding? Executor { get; init; }
    public AgentOrchestrationGateDefinition? Gate { get; init; }
    public IReadOnlyList<AgentOrchestrationGraphInputBinding> GraphInputBindings { get; init; }
        = Array.Empty<AgentOrchestrationGraphInputBinding>();
    public required string ExpectedOutputContract { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Configuration { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public AgentOrchestrationPermissionMode PermissionMode { get; init; }
        = AgentOrchestrationPermissionMode.ReadOnly;
    public AgentOrchestrationFailureBehavior FailureBehavior { get; init; }
        = AgentOrchestrationFailureBehavior.FailRun;
    public int MaxAttempts { get; init; } = 1;
    public int? TimeoutSeconds { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Versioned, pure-function predicate carried by an optional control edge (doc 83 §12.3).
/// The <see cref="AgentOrchestrationEdgeCondition"/> first checks whether the upstream node reached
/// a terminal state; the predicate then evaluates the committed output. No predicate means ordinary
/// dependency. Evaluators are version- and contract-hash-managed; arbitrary string expressions are
/// forbidden.
/// </summary>
public sealed record AgentOrchestrationEdgePredicate
{
    public required string EvaluatorId { get; init; }
    public required string Version { get; init; }
    public string? ContractHash { get; init; }
    public required string SourcePortId { get; init; }
    public string SourcePath { get; init; } = "$";
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

/// <summary>One dependency or output-visibility edge in the graph.</summary>
public sealed record AgentOrchestrationEdgeDefinition
{
    public required string EdgeId { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public required AgentOrchestrationEdgeKind Kind { get; init; }
    public AgentOrchestrationEdgeCondition Condition { get; init; }
        = AgentOrchestrationEdgeCondition.OnSuccess;
    public IReadOnlyList<AgentOrchestrationDataBinding> Bindings { get; init; }
        = Array.Empty<AgentOrchestrationDataBinding>();
    public AgentOrchestrationEdgePredicate? Predicate { get; init; }
}

/// <summary>
/// Immutable, versioned graph revision. A running instance always references GraphId + RevisionId;
/// edits create a new revision and never mutate the definition used by an existing run.
/// </summary>
public sealed record AgentOrchestrationGraphDefinition
{
    public string SchemaVersion { get; init; } = AgentOrchestrationSchemas.GraphDefinitionV2;
    public required string GraphId { get; init; }
    public required string RevisionId { get; init; }
    public int Revision { get; init; } = 1;
    public string? ParentRevisionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string RootSessionId { get; init; }
    public required string CreatedByAgentId { get; init; }
    public required string Objective { get; init; }
    public bool RequiresExplicitActivation { get; init; } = true;
    public int MaxConcurrency { get; init; } = 1;
    public IReadOnlyList<AgentOrchestrationGraphInput> Inputs { get; init; }
        = Array.Empty<AgentOrchestrationGraphInput>();
    public IReadOnlyList<AgentOrchestrationTriggerDefinition> Triggers { get; init; }
        = Array.Empty<AgentOrchestrationTriggerDefinition>();
    public IReadOnlyList<AgentOrchestrationNodeDefinition> Nodes { get; init; }
        = Array.Empty<AgentOrchestrationNodeDefinition>();
    public IReadOnlyList<AgentOrchestrationEdgeDefinition> Edges { get; init; }
        = Array.Empty<AgentOrchestrationEdgeDefinition>();
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Validation policy used when compiling a graph revision.</summary>
public sealed record AgentOrchestrationValidationOptions
{
    /// <summary>Activation candidates require an exact provider/model route for every sub-agent node.</summary>
    public bool RequireFrozenRoutes { get; init; } = true;

    /// <summary>Write-capable nodes require a later approval/policy integration and are rejected by default.</summary>
    public bool AllowExplicitWriteNodes { get; init; }
}

/// <summary>
/// Stable, machine-readable graph validation issue (doc 83 §8). Code/Message/Path are the
/// canonical compiler fields. Severity/ElementType/ElementId/PortId are optional projection
/// fields consumed by the validate API so the editor can locate the offending canvas element;
/// they stay out of the pure compile semantics when absent.
/// </summary>
public sealed record AgentOrchestrationValidationIssue(
    string Code,
    string Message,
    string? Path = null,
    string Severity = "error",
    string? ElementType = null,
    string? ElementId = null,
    string? PortId = null);

/// <summary>Result of normalization and DAG validation.</summary>
public sealed record AgentOrchestrationCompilationResult
{
    public required IReadOnlyList<AgentOrchestrationValidationIssue> Issues { get; init; }
    public AgentOrchestrationGraphDefinition? Definition { get; init; }
    public IReadOnlyList<string> TopologicalNodeIds { get; init; } = Array.Empty<string>();
    public bool Success => Definition is not null && Issues.Count == 0;
}

/// <summary>Stable event names emitted by the future durable orchestration runtime.</summary>
public static class AgentOrchestrationEventTypes
{
    public const string RunCreated = "orchestration.run.created";
    public const string RunActivated = "orchestration.run.activated";
    public const string RunAwaitingInput = "orchestration.run.awaiting_input";
    public const string RunCompleted = "orchestration.run.completed";
    public const string RunFailed = "orchestration.run.failed";
    public const string RunCancelled = "orchestration.run.cancelled";
    public const string NodeReady = "orchestration.node.ready";
    public const string NodeClaimed = "orchestration.node.claimed";
    public const string NodeClaimExpired = "orchestration.node.claim_expired";
    public const string NodeStarted = "orchestration.node.started";
    public const string NodeOutputAvailable = "orchestration.node.output.available";
    public const string NodeCompleted = "orchestration.node.completed";
    public const string NodeFailed = "orchestration.node.failed";
    public const string NodeSkipped = "orchestration.node.skipped";
}

/// <summary>
/// Append-only event envelope. Sequence is monotonic within RunId; ExecutionRunId identifies one
/// immutable attempt while SubSessionId identifies the reusable child-agent conversation.
/// </summary>
public sealed record AgentOrchestrationRunEvent
{
    public required string EventId { get; init; }
    public required string RunId { get; init; }
    public required string GraphId { get; init; }
    public required string RevisionId { get; init; }
    public long Sequence { get; init; }
    public required string EventType { get; init; }
    public string? NodeId { get; init; }
    public string? ExecutionRunId { get; init; }
    public string? SubSessionId { get; init; }
    public string? Summary { get; init; }
    public string? ArtifactReference { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Shared JSON settings for agent-authored graph files and orchestration API payloads.</summary>
public static class AgentOrchestrationJson
{
    public static JsonSerializerOptions CreateSerializerOptions(bool writeIndented = false)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
