using System.Globalization;
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

    /// <summary>用户正在接管/暂停、或已有写操作在途 ⇒ 串行化拒绝。</summary>
    public const string Busy = "browser_busy";

    /// <summary>浏览器自动化已被关闭。</summary>
    public const string Closed = "browser_closed";

    /// <summary>调用缺少宿主注入的可信控制句柄（模型不能自报所有者）。</summary>
    public const string ControlHandleRequired = "browser_control_handle_required";

    /// <summary>调用方属于另一个 Core 会话，不能使用该目标（会话归属不匹配）。</summary>
    public const string OwnerMismatch = "browser_owner_mismatch";

    /// <summary>票据已释放、已被取消，或被一次控制状态变更作废，不得再用。</summary>
    public const string PermitInvalid = "browser_permit_invalid";
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
    ControlHandleRequired,
    OwnerMismatch,
    PermitInvalid,
}

/// <summary>
/// 写世代号。**进入暂停/接管、目标作废、连接换代、关闭**时推进；
/// 退出暂停/接管**不**回退也不推进。
///
/// 为什么需要独立于 <see cref="BrowserControlEpoch"/>：控制世代只表达「目标是否作废」，
/// 而接管/暂停不撤销阅读、目标依旧有效。若没有独立写世代，接管前签发的写票据会在
/// 「接管 → 交还」之后重新通过复检（审阅报告 F7-1 的 ABA 反例）。
/// </summary>
public readonly record struct AgentTargetWriteEpoch(long Value)
{
    /// <summary>进程内唯一权威的起始写世代。</summary>
    public static readonly AgentTargetWriteEpoch Initial = new(1);

    public bool IsKnown => Value > 0;

    public AgentTargetWriteEpoch Next() => new(checked(Value + 1));

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 单张执行票据的**唯一标识**。用途：把「一次准入」与「随后的复检/完成/取消」精确关联，
/// 使迟到或重复的完成回调不可能释放另一张票据的写占用（审阅报告 F7-2）。
/// </summary>
public readonly record struct AgentTargetPermitId(Guid Value)
{
    public static AgentTargetPermitId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// 准入票据。**不是 grant，也不是 lease**：没有持有者身份、没有期限。
///
/// 它记录「判定成立时」的完整不变量快照：会话、连接世代、控制世代、**写世代**、是否副作用，
/// 以及一个只在该票据仍在途时有效的取消信号。触碰页面前必须用 <see cref="AgentTargetSessionAuthority.Revalidate"/>
/// 复检同一张票据；释放后的票据不可再用。
///
/// <paramref name="RequiresExclusiveOwnership"/> 标记这是否为**副作用**操作：
/// 接管/暂停期间副作用必须停，而只读观察继续（69 规格 §1.1「接管是运行控制」）。
/// </summary>
public sealed record AgentTargetPermit(
    AgentTargetPermitId PermitId,
    AgentTargetSessionId SessionId,
    ConnectionGeneration BridgeGeneration,
    BrowserControlEpoch ControlEpoch,
    AgentTargetWriteEpoch WriteEpoch,
    bool RequiresExclusiveOwnership,
    CancellationToken Cancellation);

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
        AgentTargetDenial.ControlHandleRequired => AgentTargetErrorCodes.ControlHandleRequired,
        AgentTargetDenial.OwnerMismatch => AgentTargetErrorCodes.OwnerMismatch,
        AgentTargetDenial.PermitInvalid => AgentTargetErrorCodes.PermitInvalid,
        _ => throw new InvalidOperationException($"unmapped denial: {Denial}"),
    };

    public static AgentTargetDecision Allow(AgentTargetPermit permit)
        => new(true, permit ?? throw new ArgumentNullException(nameof(permit)), null, null);

    public static AgentTargetDecision Deny(AgentTargetDenial denial, string message)
        => new(false, null, denial, message ?? throw new ArgumentNullException(nameof(message)));
}

/// <summary>
/// 一次待准入的请求。
///
/// <paramref name="controlHandle"/> 由**宿主注入**（工具执行边界），模型不可自报；
/// 缺失或与当前会话所有者不匹配时一律拒绝（fail-closed），见 ADR-097 D3。
/// </summary>
public sealed record AgentTargetRequest
{
    public AgentTargetRequest(
        DesktopPageTarget target,
        AgentTargetOperation operation,
        AgentTargetControlHandle? controlHandle = null,
        bool hasUserConfirmation = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        Target = target;
        Operation = operation;
        ControlHandle = controlHandle;
        HasUserConfirmation = hasUserConfirmation;
    }

    public DesktopPageTarget Target { get; }

    public AgentTargetOperation Operation { get; }

    /// <summary>宿主注入的可信控制句柄；为空 ⇒ 拒绝。</summary>
    public AgentTargetControlHandle? ControlHandle { get; }

    public bool HasUserConfirmation { get; }
}
