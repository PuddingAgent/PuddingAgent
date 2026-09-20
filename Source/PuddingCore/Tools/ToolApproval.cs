namespace PuddingCode.Tools;

/// <summary>Decision returned by the automatic high-risk tool approval layer.</summary>
public enum ToolApprovalDecision
{
    Approved,
    Denied,
    NeedHuman,

    /// <summary>
    /// ADR-091 §4.4：依赖不可用（审查模型未配置、服务不可达、审查超时/空输出/非法 JSON）。
    /// 既不是批准也不是人工决定：应持久等待依赖恢复，不得折叠成 NeedHuman 或 Denied。
    /// </summary>
    DeferredDependency,
}

/// <summary>Persisted lifecycle status for an automatic tool approval ticket.</summary>
public enum ToolApprovalTicketStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed,

    /// <summary>
    /// ADR-091 §4.4：票据因依赖不可用而未裁定（非终态，不阻断同一 invocation 重试）；
    /// 与 Pending 的区别是它不是「等人工决定」，而是「等依赖恢复」。
    /// </summary>
    DeferredDependency,
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

/// <summary>
/// 规则效果：allow（白名单语义）或 deny（黑名单语义）。
/// <para>
/// 冲突时 deny 永远优先于 allow（安全侧优先）。
/// <see cref="Allow"/> 必须保持数值 0：不含 <c>effect</c> 字段的旧 JSON 反序列化后即为 Allow，
/// 保证既有「白名单语义」记录向后兼容。
/// </para>
/// </summary>
public enum ToolApprovalRuleEffect
{
    /// <summary>放行规则（白名单语义）；缺省值，与既有行为完全兼容。</summary>
    Allow = 0,

    /// <summary>拒绝规则（黑名单语义）；与 allow 规则同键冲突时优先生效。</summary>
    Deny = 1,
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

    /// <summary>P0-6：授权时记录的工具定义规范哈希与当前定义不一致（v1 仅审计不阻断）。</summary>
    DefinitionDriftDetected,

    /// <summary>
    /// ADR-091 §4.4：依赖不可用导致的等待（非人工决定）。
    /// N01：新成员必须追加在枚举末尾，避免改变既有成员的序列化数值。
    /// </summary>
    TicketDeferredDependency,

    // —— S1（安全分类器抽象层，方案 v2 §14.8）：以下成员只允许追加在枚举末尾（N01），不得插入或重排。——

    /// <summary>安全分类器被调用一次（含对规则候选结论的覆盖裁决）。</summary>
    ClassifierInvoked,

    /// <summary>安全分类器不可用（超时 / 故障 / 未配置），已按降级契约处理。</summary>
    ClassifierUnavailable,

    /// <summary>黑名单（deny）规则被创建。</summary>
    DenylistRuleCreated,

    /// <summary>黑名单（deny）规则被禁用（不硬删除，保留审计链）。</summary>
    DenylistRuleDisabled,

    /// <summary>Agent 请求临时完全访问模式。</summary>
    FullAccessRequested,

    /// <summary>临时完全访问被授予（TTL 由服务端计时，到期自动失效）。</summary>
    FullAccessGranted,

    /// <summary>临时完全访问请求被拒绝。</summary>
    FullAccessDenied,

    /// <summary>临时完全访问因到期自动失效。</summary>
    FullAccessExpired,

    /// <summary>临时完全访问被显式撤销。</summary>
    FullAccessRevoked,

    /// <summary>同一键同时命中 allow 与 deny 规则（按冲突策略裁决并告警）。</summary>
    RuleConflictDetected,
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

    /// <summary>ADR-091 §4.4：稳定的协议原因码，供工具输出/API 统一映射。</summary>
    public string? ReasonCode { get; init; }
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

    /// <summary>ADR-091 §4.4：稳定的协议原因码（如 approval_review_timeout）；与模型自由文本分开。</summary>
    public string? ReasonCode { get; init; }
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

    /// <summary>
    /// P0-6：授权成功时捕获的工具定义规范哈希（SHA-256，见 Runtime 侧 ToolDefinitionHash.Compute）。
    /// 只存哈希不存 schema 全文（ADR-074）；旧记录缺失时为 null（向后兼容读取，容忍为空）。
    /// </summary>
    public string? DefinitionHash { get; init; }

    /// <summary>P0-6：定义版本，同一工具每次授权捕获单调 +1；0 表示未记录（旧记录缺省）。</summary>
    public int DefinitionVersion { get; init; }

    /// <summary>ADR-091 §4.4：本票据的协议原因码（引用/依赖/协议失败）；人工决定票据为 null。</summary>
    public string? ReasonCode { get; init; }
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
    /// <summary>本次执行快照的执行根（委派 worktree）；工作区文件目标解析必须与文件工具同根。</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>Result of checking a high-risk tool call against automatic approval tickets.</summary>
public sealed record ToolApprovalCheckResult
{
    public required bool IsApproved { get; init; }
    public required string Message { get; init; }
    public string? TicketId { get; init; }
    public string? AllowlistRuleId { get; init; }
    public string? ApprovalSource { get; init; }

    /// <summary>
    /// ADR-091 §4.1/F01：本次检查的 typed 终态。
    /// 调用端（Firewall/执行服务）必须依赖它区分依赖等待、人工决定与拒绝，
    /// 不得只用 IsApproved 二分，也不得把依赖等待计成 Agent 工具错误。
    /// </summary>
    public ToolApprovalDecision? Disposition { get; init; }

    /// <summary>ADR-091 §4.4：稳定的协议原因码（如 approval_review_service_unavailable）。</summary>
    public string? ReasonCode { get; init; }
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

    /// <summary>规则效果（allow / deny）；不含 effect 字段的旧记录反序列化为 Allow（向后兼容）。</summary>
    public ToolApprovalRuleEffect Effect { get; init; } = ToolApprovalRuleEffect.Allow;

    public string? ApprovedByAgentInstanceId { get; init; }
    public string? ApprovedByUserId { get; init; }
    public string? ApprovalTicketId { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public DateTimeOffset? DisabledAtUtc { get; init; }
        public long HitCount { get; init; }
    public DateTimeOffset? LastHitAtUtc { get; init; }

    /// <summary>
    /// P0-6：授权创建时捕获的工具定义规范哈希（SHA-256，见 Runtime 侧 ToolDefinitionHash.Compute）。
    /// 只存哈希不存 schema 全文（ADR-074）；旧记录/内置规则缺失时为 null（向后兼容读取）。
    /// </summary>
    public string? DefinitionHash { get; init; }

    /// <summary>P0-6：定义版本，同一工具每次授权捕获单调 +1；0 表示未记录（旧记录缺省）。</summary>
    public int DefinitionVersion { get; init; }

    // —— S2（方案 v2 §14.12.6 规则溯源 / §14.12.1 键分量 / §14.12.7 有效期）：
    // 以下成员只允许追加在记录末尾，严禁改动或重排上方既有属性；旧 JSON 缺失时反序列化为 null/0，完全向后兼容。——

    /// <summary>产出（或最近刷新）本规则的分类器稳定标识（§14.12.6）；非分类器产出的规则为 null。</summary>
    public string? SourceClassifierId { get; init; }

    /// <summary>产出本规则时分类器使用的模型标识；纯规则类分类器为 null。</summary>
    public string? ClassifierModel { get; init; }

    /// <summary>裁决结论的逐分类可信度（0..1）；缺失为 null（视为低于阈值，按 §14.12.7 建议有效期）。</summary>
    public double? OutcomeConfidence { get; init; }

    /// <summary>创建本规则时的会话 id（§14.12.6 CreatedBySessionId；Agent/用户/出题单复用上方既有三个 ApprovedBy*/ApprovalTicketId 字段）。</summary>
    public string? CreatedBySessionId { get; init; }

    /// <summary>首次沉淀时间（UTC，§14.12.6）；幂等更新时保持不变。</summary>
    public DateTimeOffset? FirstSeenAtUtc { get; init; }

    /// <summary>最近一次策展刷新时间（UTC，§14.12.3/§14.12.6）；与 LastHitAtUtc（快路径命中）语义不同。</summary>
    public DateTimeOffset? LastSeenAtUtc { get; init; }

    /// <summary>§14.12.7 可选有效期：置信度 &lt; 0.95 时策展器建议 30 天；到期由消费方仅标记 Disabled，启动不自动清理、不硬删除。</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>规则键 working_directory 分量（§14.12.1，分隔符统一、去尾分隔符后的规范化值）；null 表示无工作目录约束。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>规则键 shell 分量（§14.12.1，trim 后原样保留大小写）；非 shell 类工具为 null。</summary>
    public string? Shell { get; init; }
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

    /// <summary>规则效果（allow / deny）；缺省 Allow，与既有变更请求兼容。</summary>
    public ToolApprovalRuleEffect Effect { get; init; } = ToolApprovalRuleEffect.Allow;
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

    /// <summary>涉及的规则效果（allow / deny）；非规则类事件保持缺省 Allow。</summary>
    public ToolApprovalRuleEffect Effect { get; init; } = ToolApprovalRuleEffect.Allow;

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
