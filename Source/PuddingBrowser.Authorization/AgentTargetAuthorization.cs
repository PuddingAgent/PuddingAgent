using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Authorization;

/// <summary>
/// 调用方声明的一次操作类别。
///
/// 分类责任在**结构化动作层**（工具侧），不在本组件：本组件只按类别施加规则，
/// 且对不可靠分类采取 fail-closed —— 见 <see cref="AgentTargetOperationExtensions.RequiresUserConfirmation"/>。
/// </summary>
public enum AgentTargetOperation
{
    /// <summary>只读观察：快照、定位、列出标签页。接管期间**仍然允许**（接管不撤销阅读）。</summary>
    Observe,

    /// <summary>导航。属副作用。</summary>
    Navigate,

    /// <summary>一般交互：点击、填表、按键。属副作用。</summary>
    Interact,

    /// <summary>一般写入。属副作用。</summary>
    Mutate,

    /// <summary>高风险不可逆写入：支付/下单/转账、发布/发送/提交、账户安全与身份认证类。</summary>
    HighRiskAction,

    /// <summary>浏览器目标管理：新建/关闭 context 或标签页。属副作用。</summary>
    ManageTarget,
}

public static class AgentTargetOperationExtensions
{
    /// <summary>是否有副作用（接管/暂停期间一律拒绝）。</summary>
    public static bool IsSideEffect(this AgentTargetOperation operation) => operation != AgentTargetOperation.Observe;

    /// <summary>
    /// 是否需要用户逐次确认。
    ///
    /// 只有 <see cref="AgentTargetOperation.HighRiskAction"/> 需要。
    /// **分类层必须把无法可靠判定的不可逆写操作也归入 HighRiskAction**（fail-closed），
    /// 而不是归入 Mutate 以求方便。
    /// </summary>
    public static bool RequiresUserConfirmation(this AgentTargetOperation operation)
        => operation == AgentTargetOperation.HighRiskAction;
}

/// <summary>稳定拒绝码。UI 与工具错误必须复用这些字面量，禁止各自拼字符串（历史上同一意图出现过三种拒绝码）。</summary>
public static class AgentTargetErrorCodes
{
    /// <summary>桥未连接 / 世代已换代。</summary>
    public const string TransportUnavailable = "browser_transport_unavailable";

    /// <summary>用户尚未把该页设为 Agent 目标（或目标不是这一页）。</summary>
    public const string TargetRequired = "agent_target_required";

    /// <summary>曾经有过目标，但已被撤销 / 会话已作废。</summary>
    public const string TargetRevoked = "agent_target_revoked";

    /// <summary>高风险动作缺少用户逐次确认。</summary>
    public const string UserConfirmationRequired = "user_confirmation_required";

    /// <summary>用户正在接管、或已有写操作在途 ⇒ 串行化拒绝。</summary>
    public const string Busy = "browser_busy";

    /// <summary>浏览器自动化已被关闭。</summary>
    public const string Closed = "browser_closed";
}

/// <summary>拒绝原因（与稳定码一一对应）。</summary>
public enum AgentTargetDenial
{
    TransportUnavailable,
    TargetRequired,
    TargetRevoked,
    UserConfirmationRequired,
    Busy,
    Closed,
}

/// <summary>
/// 准入票据。**不是 grant，也不是 lease**：没有持有者身份、没有期限、没有所有权，
/// 唯一用途是让「判定」与「随后触碰页面」之间可以复检竞态（世代 + 控制世代 + 会话）。
///
/// <paramref name="RequiresExclusiveOwnership"/> 标记这是否为**副作用**操作：
/// 接管/暂停期间副作用必须停，而只读观察继续（69 规格 §1.1「接管是运行控制」）。
/// </summary>
public sealed record AgentTargetPermit(
    AgentTargetSessionId SessionId,
    ConnectionGeneration BridgeGeneration,
    BrowserControlEpoch ControlEpoch,
    bool RequiresExclusiveOwnership);

/// <summary>一次准入判定的结果。</summary>
public sealed record AgentTargetDecision
{
    private AgentTargetDecision(bool isAllowed, AgentTargetPermit? permit, AgentTargetDenial? denial, string? message)
    {
        IsAllowed = isAllowed;
        Permit = permit;
        Denial = denial;
        Message = message;
    }

    public bool IsAllowed { get; }

    public AgentTargetPermit? Permit { get; }

    public AgentTargetDenial? Denial { get; }

    public string? Message { get; }

    /// <summary>稳定拒绝码；允许时为 null。</summary>
    public string? ErrorCode => Denial switch
    {
        null => null,
        AgentTargetDenial.TransportUnavailable => AgentTargetErrorCodes.TransportUnavailable,
        AgentTargetDenial.TargetRequired => AgentTargetErrorCodes.TargetRequired,
        AgentTargetDenial.TargetRevoked => AgentTargetErrorCodes.TargetRevoked,
        AgentTargetDenial.UserConfirmationRequired => AgentTargetErrorCodes.UserConfirmationRequired,
        AgentTargetDenial.Busy => AgentTargetErrorCodes.Busy,
        AgentTargetDenial.Closed => AgentTargetErrorCodes.Closed,
        _ => throw new InvalidOperationException($"unmapped denial: {Denial}"),
    };

    public static AgentTargetDecision Allow(AgentTargetPermit permit)
        => new(true, permit ?? throw new ArgumentNullException(nameof(permit)), null, null);

    public static AgentTargetDecision Deny(AgentTargetDenial denial, string message)
        => new(false, null, denial, message ?? throw new ArgumentNullException(nameof(message)));
}

/// <summary>一次待准入的请求。</summary>
public sealed record AgentTargetRequest
{
    public AgentTargetRequest(
        DesktopPageTarget target,
        AgentTargetOperation operation,
        bool hasUserConfirmation = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        Target = target;
        Operation = operation;
        HasUserConfirmation = hasUserConfirmation;
    }

    public DesktopPageTarget Target { get; }

    public AgentTargetOperation Operation { get; }

    public bool HasUserConfirmation { get; }
}
