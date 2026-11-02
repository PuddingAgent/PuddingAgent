namespace Pudding.Contracts.Computer;

/// <summary>
/// 稳定错误码。前缀统一 <c>computer_</c>，**码值是契约的一部分**：模型与前端按它做本地化，
/// 改字符串等于改协议。
/// <para>
/// 分组的含义在注释里，因为"同一组里的码通常对应同一种下一步"：
/// 授权类要重新申请，失效类要重新观察，执行类要查终态而不是重试。
/// </para>
/// </summary>
public static class ComputerErrorCodes
{
    // ── 通道与能力
    public const string NotConnected = "computer_not_connected";
    public const string UnsupportedCapability = "computer_unsupported_capability";

    // ── 授权：需要用户重新给，不是重试能解决的
    public const string Unauthorized = "computer_unauthorized";
    public const string GrantExpired = "computer_grant_expired";
    public const string GrantRevoked = "computer_grant_revoked";

    // ── 控制权冲突：有别人在用，或用户接管了
    public const string DesktopBusy = "computer_desktop_busy";
    public const string Paused = "computer_paused";
    public const string UserTakeover = "computer_user_takeover";

    // ── 桌面环境不可用
    public const string DesktopUnavailable = "computer_desktop_unavailable";
    public const string SecureDesktop = "computer_secure_desktop";
    public const string IntegrityMismatch = "computer_integrity_mismatch";

    // ── 目标身份：关闭/复用/多义，都要求重新确定目标
    public const string TargetClosed = "computer_target_closed";
    public const string TargetReused = "computer_target_reused";
    public const string TargetAmbiguous = "computer_target_ambiguous";

    // ── 引用失效：必须重新观察，绝不能拿旧 ref 重试
    public const string StaleObservation = "computer_stale_observation";
    public const string StaleElement = "computer_stale_element";
    public const string CursorExpired = "computer_cursor_expired";

    // ── 输入前置条件不满足
    public const string ForegroundMismatch = "computer_foreground_mismatch";
    public const string PointObstructed = "computer_point_obstructed";

    // ── 该后端没有这个能力：明确拒绝，不回退到另一条路
    public const string PatternUnsupported = "computer_pattern_unsupported";
    public const string UnsupportedAction = "computer_unsupported_action";

    // ── 已经开始执行：保留回执，查终态，不重发
    public const string PartialInput = "computer_partial_input";
    public const string VerificationFailed = "computer_verification_failed";
    public const string OutcomeUnknown = "computer_outcome_unknown";

    // ── 执行器或资源状态异常
    public const string WorkerUnresponsive = "computer_worker_unresponsive";
    public const string OperationConflict = "computer_operation_conflict";
    public const string BudgetExceeded = "computer_budget_exceeded";
}
