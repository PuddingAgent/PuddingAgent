using System.Globalization;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Authorization;

/// <summary>
/// 目标会话标识。仅用于把「一次授权判定」与「随后的一次操作」关联起来，不承载权限语义。
/// </summary>
public readonly record struct AgentTargetSessionId(Guid Value)
{
    public static AgentTargetSessionId New() => new(Guid.NewGuid());

    public static AgentTargetSessionId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("session id must not be empty", nameof(value))
        : new AgentTargetSessionId(value);

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// **Core 会话所有者**（不透明标识，由宿主注入）。
///
/// 它回答「这个目标会话属于哪一次 Core 会话」，使无关会话不能复用用户交出的页面。
/// 它不是 Agent 展示名，也不来自模型参数：宿主从可信上下文取值，模型只选择被发现的 page/context。
/// </summary>
public readonly record struct AgentTargetOwnerId
{
    public const int MaxLength = 128;

    private AgentTargetOwnerId(string value) => Value = value;

    public string Value { get; }

    /// <summary>由宿主传入的标识构造。空白、过长或含控制字符时抛错（不可由页面/模型内容构造）。</summary>
    public static AgentTargetOwnerId Require(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("owner id must not be empty", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"owner id must be at most {MaxLength} characters", nameof(value));
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("owner id must not contain control characters", nameof(value));
        }

        return new AgentTargetOwnerId(trimmed);
    }

    public override string ToString() => Value;
}

/// <summary>
/// 宿主注入的**可信控制句柄**：一次工具调用所属的 Core 会话所有者 + 仅供诊断的展示名。
///
/// 展示名缺失不是拒绝理由（诊断字段），但句柄本身缺失或所有者不匹配必须拒绝。
/// 模型不能自报该句柄。
/// </summary>
public sealed record AgentTargetControlHandle
{
    public const int MaxDisplayNameLength = 160;

    public AgentTargetControlHandle(AgentTargetOwnerId owner, string? displayName = null)
    {
        Owner = owner;
        DisplayName = NormalizeDisplayName(displayName);
    }

    public AgentTargetOwnerId Owner { get; }

    /// <summary>仅用于 UI/审计展示，不参与任何授权判定。</summary>
    public string? DisplayName { get; }

    public static AgentTargetControlHandle For(AgentTargetOwnerId owner) => new(owner);

    public static AgentTargetControlHandle For(string ownerId, string? displayName = null)
        => new(AgentTargetOwnerId.Require(ownerId), displayName);

    private static string? NormalizeDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var cleaned = new string(displayName.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length <= MaxDisplayNameLength
            ? cleaned
            : cleaned[..MaxDisplayNameLength];
    }
}

/// <summary>
/// **唯一的授权载体**：用户显式选中的一页 + 该选择成立时的桥世代 + 该选择归属的 Core 会话所有者。
///
/// 刻意不包含：逐调用身份、任务/父任务、读写分级、过期时间、租约。
/// 权限只由「用户是否把这一页交给这个会话」决定；身份维度由宿主作为可信控制句柄注入（见 ADR-097 D3）。
/// </summary>
public sealed record AgentTargetSession
{
    public AgentTargetSession(
        AgentTargetSessionId sessionId,
        DesktopPageTarget target,
        ConnectionGeneration bridgeGeneration,
        AgentTargetControlHandle controlHandle,
        DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(controlHandle);
        if (!bridgeGeneration.IsLive)
        {
            throw new ArgumentException(
                "a target session must be bound to a live bridge generation; a dead generation could not be validated.",
                nameof(bridgeGeneration));
        }

        SessionId = sessionId;
        Target = target;
        BridgeGeneration = bridgeGeneration;
        ControlHandle = controlHandle;
        StartedAtUtc = startedAtUtc;
    }

    public AgentTargetSessionId SessionId { get; }

    public DesktopPageTarget Target { get; }

    /// <summary>选择成真时的桥世代。桥换代 ⇒ 本会话作废（见 <see cref="AgentTargetDenial.TransportUnavailable"/>）。</summary>
    public ConnectionGeneration BridgeGeneration { get; }

    /// <summary>宿主注入的可信会话所有者。换所有者 ⇒ 旧会话作废，不自动继承执行权限。</summary>
    public AgentTargetControlHandle ControlHandle { get; }

    public AgentTargetOwnerId Owner => ControlHandle.Owner;

    public DateTimeOffset StartedAtUtc { get; }
}

/// <summary>
/// 运行控制状态（**不是权限审批**）。暂停与接管是两个**独立轴**：进入任一只阻断副作用，
/// 退出一个不会清除另一个；两轴都不撤销阅读（69 规格 §1.1）。
/// </summary>
[Flags]
public enum AgentTargetControlState
{
    None = 0,

    /// <summary>用户暂停了 Agent 的浏览器操作。</summary>
    Paused = 1,

    /// <summary>用户正在接管桌面浏览器（手动操作）。</summary>
    UserTakeover = 2,
}

/// <summary>控制面快照。UI/诊断只读它，不得各自维护一份状态（历史上三份漂移过）。</summary>
public sealed record AgentTargetSessionSnapshot(
    bool IsBridgeReady,
    ConnectionGeneration BridgeGeneration,
    BrowserControlEpoch ControlEpoch,
    AgentTargetWriteEpoch WriteEpoch,
    AgentTargetSession? Session,
    DesktopPageTarget? PendingTarget,
    AgentTargetControlState ControlState,
    bool IsClosed,
    AgentTargetSessionId? LastRevokedSessionId,
    string? Reason)
{
    public bool HasTarget => Session is not null;

    /// <summary>用户已交出页面、但归属尚未确认（UI 应显示“等待会话确认”，而不是“已交出”）。</summary>
    public bool HasPendingHandover => PendingTarget is not null;

    public bool IsPaused => ControlState.HasFlag(AgentTargetControlState.Paused);

    public bool IsUserControlling => ControlState.HasFlag(AgentTargetControlState.UserTakeover);

    /// <summary>任一阻断状态 ⇒ 副作用操作不得执行（只读仍允许）。</summary>
    public bool IsWriteBlocked => ControlState != AgentTargetControlState.None;

    /// <summary>给 UI 的一行状态：连接态 + 目标态 + 运行控制态，避免用户点了按钮却看不到任何变化。</summary>
    public string Describe()
    {
        var bridge = IsBridgeReady
            ? string.Concat("Connected(gen=", BridgeGeneration.Value.ToString(CultureInfo.InvariantCulture), ")")
            : "Disconnected";
        var target = Session is not null
            ? string.Concat("Target=", Session.Target.Key)
            : PendingTarget is not null
                ? string.Concat("PendingHandover=", PendingTarget.Key)
                : "NoTarget";
        var control = IsClosed
            ? "Closed"
            : ControlState switch
            {
                AgentTargetControlState.None => "AgentControllable",
                AgentTargetControlState.Paused => "Paused",
                AgentTargetControlState.UserTakeover => "UserControlling",
                _ => "Paused+UserControlling",
            };
        return string.Concat(bridge, " · ", target, " · ", control);
    }
}
