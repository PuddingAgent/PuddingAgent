namespace PuddingCode.Tools;

/// <summary>Decision returned by the automatic high-risk tool approval layer.</summary>
public enum ToolApprovalDecision
{
    Approved,
    Denied,
    NeedHuman,
}

/// <summary>Persisted lifecycle status for an automatic tool approval ticket.</summary>
public enum ToolApprovalTicketStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed,
}

/// <summary>Lifetime granted to an automatic tool approval ticket.</summary>
public enum ToolApprovalScope
{
    Once,
    Session,
    Timed,
}

/// <summary>Shape of an automatic approval ticket.</summary>
public enum ToolApprovalTicketKind
{
    SingleInvocation,
    Job,
    RuleProposal,
}

/// <summary>How strongly the request is backed by human consent.</summary>
public enum ToolApprovalUserConsentStatus
{
    Explicit,
    Implied,
    Absent,
    Unknown,
}

/// <summary>Source that created an automatic approval allowlist rule.</summary>
public enum ToolApprovalAllowlistRuleSource
{
    BuiltIn,
    AuditAgent,
    Human,
}

/// <summary>Lifecycle state for an automatic approval allowlist rule.</summary>
public enum ToolApprovalAllowlistRuleStatus
{
    Enabled,
    Disabled,
}

/// <summary>Audit event category for automatic approval decisions and allowlist activity.</summary>
public enum ToolApprovalAuditEventType
{
    TicketSubmitted,
    TicketApproved,
    TicketDenied,
    TicketNeedHuman,
    TicketMatched,
    TicketConsumed,
    TicketMismatch,
    ImplicitApproved,
    ImplicitDenied,
    AllowlistHit,
    AllowlistRuleCreated,
    AllowlistRuleUpdated,
    AllowlistRuleDisabled,
}

/// <summary>Identity boundary for submitting or checking an automatic tool approval ticket.</summary>
public sealed record ToolApprovalIdentity
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? AgentTemplateId { get; init; }
    public required string UserId { get; init; }
}

/// <summary>One planned operation step in an approval ticket checklist.</summary>
public sealed record ToolApprovalOperationStep
{
    public required int StepNumber { get; init; }
    public string? ToolId { get; init; }
    public required string Command { get; init; }
    public string? RequestedArgumentsJson { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Environment { get; init; }
    public required string TargetObject { get; init; }
    public required string Purpose { get; init; }
    public required string ExpectedEffect { get; init; }
    public required string Reasonableness { get; init; }
    public string? SafetyCheckBefore { get; init; }
    public required string StopCondition { get; init; }
    public string? RollbackForStep { get; init; }
    public int? AllowedInvocationCount { get; init; }
}

/// <summary>Structured checklist submitted by an agent before using a high-risk tool.</summary>
public sealed record ToolApprovalTicketRequest
{
    public ToolApprovalTicketKind TicketKind { get; init; } = ToolApprovalTicketKind.SingleInvocation;
    public required string ToolId { get; init; }
    public string? CommandName { get; init; }
    public string Purpose { get; init; } = "";
    public string Necessity { get; init; } = "";
    public IReadOnlyList<string> FactBasis { get; init; } = [];
    public string? RequestedArgumentsJson { get; init; }
    public IReadOnlyList<string> TargetResources { get; init; } = [];
    public IReadOnlyList<string> AuthorizedArea { get; init; } = [];
    public string? OutsideAuthorizedAreaReason { get; init; }
    public bool MayDamageOrDeleteData { get; init; }
    public bool IsIrreversibleOperation { get; init; }
    public bool BackupTaken { get; init; }
    public string? RollbackPlan { get; init; }
    public string OperationContext { get; init; } = "";
    public string? OperationPlan { get; init; }
    public IReadOnlyList<ToolApprovalOperationStep> OperationSteps { get; init; } = [];
    public string? TemporaryFileEvidence { get; init; }
    public bool MayExposeSecrets { get; init; }
    public ToolApprovalUserConsentStatus UserConsentStatus { get; init; } = ToolApprovalUserConsentStatus.Unknown;
    public IReadOnlyList<string> AlternativesConsidered { get; init; } = [];
    public ToolApprovalScope RequestedScope { get; init; } = ToolApprovalScope.Once;
    public TimeSpan? RequestedDuration { get; init; }
    public string? RiskNotes { get; init; }
    public bool RequestAllowlistRule { get; init; }
    public string? AllowlistReason { get; init; }
}

/// <summary>Result returned after submitting an automatic tool approval ticket.</summary>
public sealed record ToolApprovalTicketResult
{
    public required string TicketId { get; init; }
    public required ToolApprovalDecision Decision { get; init; }
    public required ToolApprovalTicketStatus Status { get; init; }
    public required string DecisionReason { get; init; }
    public ToolApprovalScope? AllowedScope { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public string? RecommendedNextStep { get; init; }
    public string? AllowlistRuleId { get; init; }
}

/// <summary>Decision returned by the approval reviewer before a ticket is stored.</summary>
public sealed record ToolApprovalReviewResult
{
    public required ToolApprovalDecision Decision { get; init; }
    public required string DecisionReason { get; init; }
    public ToolApprovalScope? AllowedScope { get; init; }
    public TimeSpan? AllowedDuration { get; init; }
    public bool RequiresHumanAuthorization { get; init; }
    public IReadOnlyList<string> ChecklistFindings { get; init; } = [];
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
    public IReadOnlyList<ToolApprovalAllowlistProposal> AllowlistProposals { get; init; } = [];
    public string? RecommendedFix { get; init; }
    public string? ReviewerModel { get; init; }
}

/// <summary>Reusable command or argument shape proposed by the reviewer after approving a ticket.</summary>
public sealed record ToolApprovalAllowlistProposal
{
    public string? ToolId { get; init; }
    public string? Command { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Stored approval ticket state used by runtime checks and future persistence.</summary>
public sealed record ToolApprovalTicketRecord
{
    public required string TicketId { get; init; }
    public required ToolApprovalIdentity Identity { get; init; }
    public required string ToolId { get; init; }
    public ToolApprovalTicketRequest? Request { get; init; }
    public required string ArgumentsHash { get; init; }
    public required ToolApprovalScope Scope { get; init; }
    public required ToolApprovalTicketStatus Status { get; init; }
    public required string DecisionReason { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? DecidedAtUtc { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public int? RemainingUses { get; init; }
    public DateTimeOffset? ConsumedAtUtc { get; init; }
}

/// <summary>Actual high-risk tool call checked against approved automatic tickets.</summary>
public sealed record ToolApprovalExecutionRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string UserId { get; init; }
    public required string ToolId { get; init; }
    public string? ActualArgumentsJson { get; init; }
}

/// <summary>Result of checking a high-risk tool call against automatic approval tickets.</summary>
public sealed record ToolApprovalCheckResult
{
    public required bool IsApproved { get; init; }
    public required string Message { get; init; }
    public string? TicketId { get; init; }
    public string? AllowlistRuleId { get; init; }
    public string? ApprovalSource { get; init; }
}

/// <summary>Exact command or argument rule used to fast-approve low-risk tool calls.</summary>
public sealed record ToolApprovalAllowlistRule
{
    public required string RuleId { get; init; }
    public string? WorkspaceId { get; init; }
    public required string ToolId { get; init; }
    public string? Command { get; init; }
    public string? ArgumentsJson { get; init; }
    public ToolApprovalAllowlistRuleSource Source { get; init; } = ToolApprovalAllowlistRuleSource.Human;
    public ToolApprovalAllowlistRuleStatus Status { get; init; } = ToolApprovalAllowlistRuleStatus.Enabled;
    public string? ApprovedByAgentInstanceId { get; init; }
    public string? ApprovedByUserId { get; init; }
    public string? ApprovalTicketId { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public DateTimeOffset? DisabledAtUtc { get; init; }
    public long HitCount { get; init; }
    public DateTimeOffset? LastHitAtUtc { get; init; }
}

/// <summary>Mutation request for a tool approval allowlist rule.</summary>
public sealed record ToolApprovalAllowlistRuleMutation
{
    public string? WorkspaceId { get; init; }
    public required string ToolId { get; init; }
    public string? Command { get; init; }
    public string? ArgumentsJson { get; init; }
    public ToolApprovalAllowlistRuleSource Source { get; init; } = ToolApprovalAllowlistRuleSource.Human;
    public string? ApprovedByAgentInstanceId { get; init; }
    public string? ApprovedByUserId { get; init; }
    public string? ApprovalTicketId { get; init; }
    public string? Reason { get; init; }
    public ToolApprovalAllowlistRuleStatus Status { get; init; } = ToolApprovalAllowlistRuleStatus.Enabled;
}

/// <summary>Recorded audit event for approval reviewer decisions and allowlist usage.</summary>
public sealed record ToolApprovalAuditEvent
{
    public required string EventId { get; init; }
    public required ToolApprovalAuditEventType EventType { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? UserId { get; init; }
    public string? ToolId { get; init; }
    public string? Command { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? OriginalCommand { get; init; }
    public string? OriginalArgumentsJson { get; init; }
    public string? TicketId { get; init; }
    public string? AllowlistRuleId { get; init; }
    public string? AllowlistRuleCommand { get; init; }
    public string? AllowlistRuleArgumentsJson { get; init; }
    public long? AllowlistRuleHitCount { get; init; }
    public ToolApprovalDecision? Decision { get; init; }
    public ToolApprovalAllowlistRuleSource? Source { get; init; }
    public string? ReviewerModel { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>Stores automatic approval tickets.</summary>
public interface IToolApprovalTicketStore
{
    Task SaveAsync(ToolApprovalTicketRecord ticket, CancellationToken ct = default);

    Task<ToolApprovalTicketRecord?> GetAsync(string ticketId, CancellationToken ct = default);

    Task<IReadOnlyList<ToolApprovalTicketRecord>> ListAsync(CancellationToken ct = default);
}

/// <summary>Stores exact allowlist rules for fast automatic approval.</summary>
public interface IToolApprovalAllowlistStore
{
    Task SaveAsync(ToolApprovalAllowlistRule rule, CancellationToken ct = default);

    Task<ToolApprovalAllowlistRule?> GetAsync(string ruleId, CancellationToken ct = default);

    Task<IReadOnlyList<ToolApprovalAllowlistRule>> ListAsync(CancellationToken ct = default);
}

/// <summary>Stores automatic approval audit events for tracking and statistics.</summary>
public interface IToolApprovalAuditStore
{
    Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default);

    Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default);
}

/// <summary>Reviews structured approval requests and decides whether a ticket can be issued.</summary>
public interface IToolApprovalReviewer
{
    Task<ToolApprovalReviewResult> ReviewAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default);
}

/// <summary>Automatic high-risk tool approval service.</summary>
public interface IToolApprovalService
{
    Task<ToolApprovalTicketResult> SubmitAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default);

    Task<ToolApprovalCheckResult> CheckAsync(
        ToolApprovalExecutionRequest request,
        ToolDescriptor descriptor,
        CancellationToken ct = default);
}
