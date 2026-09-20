namespace PuddingCode.Tools;

/// <summary>
/// ADR-091 §4.4：审批决策/状态在外部出口（工具输出、API、事件）的 wire 名称与稳定原因码。
/// 服务端判定与模型自由文本分开，避免 <c>deferreddependency</c> 与 <c>deferred_dependency</c>
/// 在不同出口漂移；原因码统一从这里取，便于查询、过滤与跨重启区分。
/// </summary>
public static class ToolApprovalWire
{
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string NeedHuman = "need_human";
    public const string DeferredDependency = "deferred_dependency";

    /// <summary>未配置审查 profile（依赖缺失，可恢复）。</summary>
    public const string CodeProfileNotConfigured = "approval_review_profile_not_configured";

    /// <summary>profile 解析器报错。</summary>
    public const string CodeResolutionFailed = "approval_review_profile_resolution_failed";

    /// <summary>审查模型调用服务不可用（未注册或不可达）。</summary>
    public const string CodeServiceUnavailable = "approval_review_service_unavailable";

    /// <summary>模型调用失败。</summary>
    public const string CodeCallFailed = "approval_review_call_failed";

    /// <summary>审查自身 deadline 到期。</summary>
    public const string CodeTimeout = "approval_review_timeout";

    /// <summary>模型返回空响应。</summary>
    public const string CodeEmptyResponse = "approval_review_empty_response";

    /// <summary>响应不是合法 JSON。</summary>
    public const string CodeInvalidJson = "approval_review_invalid_json";

    /// <summary>JSON 根不是对象。</summary>
    public const string CodeNonObjectRoot = "approval_review_non_object_root";

    /// <summary>decision 缺失或不是已知枚举值。</summary>
    public const string CodeUnknownDecision = "approval_review_unknown_decision";

    /// <summary>缺少非空 reason。</summary>
    public const string CodeMissingReason = "approval_review_missing_reason";

    /// <summary>跨字段语义矛盾（如 approved + requiresHumanAuthorization=true）。</summary>
    public const string CodeContradictory = "approval_review_contradictory_fields";

    /// <summary>字段类型不符合 schema。</summary>
    public const string CodeInvalidFieldType = "approval_review_invalid_field_type";

    /// <summary>
    /// 评审器声明了 <c>deferred_dependency</c> 但未给出 reasonCode（schema 不完整）。
    /// 由 <c>ToolApprovalReviewParser</c> 合成，用于保证「依赖等待必带稳定码」的不变量：
    /// 调用方应能仅凭 reasonCode 分支，而不必解析自由文本 reason。
    /// </summary>
    public const string CodeDeferredReasonCodeMissing = "approval_review_deferred_reason_code_missing";

    /// <summary>
    /// 安全分类器无法给出四选一结论（ClassificationOutcome.Unknown：输入不足、规则类实现未命中、
    /// 仲裁不可用）时的依赖等待原因码（方案 v2 §14.5/§14.7）：按降级契约转 DeferredDependency，
    /// 绝不得折叠为批准、拒绝或人工。本文件是稳定原因码的单一来源：新增原因码一律在此追加。
    /// </summary>
    public const string CodeClassifierUnknown = "approval_review_classifier_unknown";

    /// <summary>决策的 wire 名称。</summary>
    public static string ToWire(ToolApprovalDecision decision) => decision switch
    {
        ToolApprovalDecision.Approved => Approved,
        ToolApprovalDecision.Denied => Denied,
        ToolApprovalDecision.NeedHuman => NeedHuman,
        ToolApprovalDecision.DeferredDependency => DeferredDependency,
        _ => "unknown",
    };

    /// <summary>票据状态的 wire 名称。</summary>
    public static string ToWire(ToolApprovalTicketStatus status) => status switch
    {
        ToolApprovalTicketStatus.Pending => "pending",
        ToolApprovalTicketStatus.Approved => Approved,
        ToolApprovalTicketStatus.Denied => Denied,
        ToolApprovalTicketStatus.Expired => "expired",
        ToolApprovalTicketStatus.Consumed => "consumed",
        ToolApprovalTicketStatus.DeferredDependency => DeferredDependency,
        _ => "unknown",
    };

    /// <summary>
    /// 审计事件类型的 wire 名称（<b>稳定契约</b>）。
    /// <para>
    /// 为何放在这里：本文件的存在宗旨就是“避免同一概念在不同出口漂移”（见类注释）。
    /// 历史缺陷（2026-09-21 发现）：管理 API 曾自带一份**只有 13 条分支**的私有映射，
    /// 而枚举有 **26 个成员** ⇒ 其余 13 个事件落到 <c>ToString().ToLowerInvariant()</c> 兜底，
    /// 产生 <c>classifierinvoked</c> / <c>fullaccessgatebypass</c> 这类**丢下划线**的名字，
    /// 与 <c>ticket_submitted</c> 形成两套命名混在同一字段；且管理 API 的
    /// <c>eventType</c> 过滤正是拿本名字比对 ⇒ 那些事件**筛选不出来**。
    /// 本映射在功能**尚未部署**时修正，因此无历史消费者需要兼容。
    /// </para>
    /// <para>
    /// 新增枚举成员时（N01：只允许追加在末尾）**必须在此补一条显式映射**；
    /// 兜底只保证“不静默说错”（如实回显枚举名），不保证命名风格，因此不得依赖它。
    /// </para>
    /// </summary>
    public static string ToWire(ToolApprovalAuditEventType eventType) => eventType switch
    {
        ToolApprovalAuditEventType.TicketSubmitted => "ticket_submitted",
        ToolApprovalAuditEventType.TicketApproved => "ticket_approved",
        ToolApprovalAuditEventType.TicketDenied => "ticket_denied",
        ToolApprovalAuditEventType.TicketNeedHuman => "ticket_need_human",
        ToolApprovalAuditEventType.TicketMatched => "ticket_matched",
        ToolApprovalAuditEventType.TicketConsumed => "ticket_consumed",
        ToolApprovalAuditEventType.TicketMismatch => "ticket_mismatch",
        ToolApprovalAuditEventType.ImplicitApproved => "implicit_approved",
        ToolApprovalAuditEventType.ImplicitDenied => "implicit_denied",
        ToolApprovalAuditEventType.AllowlistHit => "allowlist_hit",
        ToolApprovalAuditEventType.AllowlistRuleCreated => "allowlist_rule_created",
        ToolApprovalAuditEventType.AllowlistRuleUpdated => "allowlist_rule_updated",
        ToolApprovalAuditEventType.AllowlistRuleDisabled => "allowlist_rule_disabled",
        ToolApprovalAuditEventType.DefinitionDriftDetected => "definition_drift_detected",
        ToolApprovalAuditEventType.TicketDeferredDependency => "ticket_deferred_dependency",
        ToolApprovalAuditEventType.ClassifierInvoked => "classifier_invoked",
        ToolApprovalAuditEventType.ClassifierUnavailable => "classifier_unavailable",
        ToolApprovalAuditEventType.DenylistRuleCreated => "denylist_rule_created",
        ToolApprovalAuditEventType.DenylistRuleDisabled => "denylist_rule_disabled",
        ToolApprovalAuditEventType.FullAccessRequested => "full_access_requested",
        ToolApprovalAuditEventType.FullAccessGranted => "full_access_granted",
        ToolApprovalAuditEventType.FullAccessDenied => "full_access_denied",
        ToolApprovalAuditEventType.FullAccessExpired => "full_access_expired",
        ToolApprovalAuditEventType.FullAccessRevoked => "full_access_revoked",
        ToolApprovalAuditEventType.RuleConflictDetected => "rule_conflict_detected",
        ToolApprovalAuditEventType.FullAccessGateBypass => "full_access_gate_bypass",
        // 兜底只保证不静默说错（如实回显枚举名）；命名风格由上方显式映射负责。
        _ => eventType.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// 解析模型返回的 decision。接受 <c>-</c>/<c>_</c> 与大小写差异，以及历史 camel 写法，
    /// 但未知值一律失败（由调用方转成协议失败，不得默认放行或默认人工）。
    /// </summary>
    public static bool TryParseDecision(string? value, out ToolApprovalDecision decision)
    {
        decision = ToolApprovalDecision.DeferredDependency;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        switch (normalized)
        {
            case Approved:
                decision = ToolApprovalDecision.Approved;
                return true;
            case Denied:
                decision = ToolApprovalDecision.Denied;
                return true;
            case NeedHuman:
            case "needhuman":
                decision = ToolApprovalDecision.NeedHuman;
                return true;
            case DeferredDependency:
            case "deferreddependency":
                decision = ToolApprovalDecision.DeferredDependency;
                return true;
            default:
                return false;
        }
    }
}
