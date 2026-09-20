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
