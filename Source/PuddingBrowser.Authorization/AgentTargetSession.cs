using System.Globalization;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Authorization;

/// <summary>目标会话标识。仅用于把「一次授权判定」与「随后的一次操作」关联起来，不承载权限语义。</summary>
public readonly record struct AgentTargetSessionId(Guid Value)
{
    public static AgentTargetSessionId New() => new(Guid.NewGuid());

    public static AgentTargetSessionId From(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("session id must not be empty", nameof(value))
        : new AgentTargetSessionId(value);

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// **唯一的授权载体**：用户显式选中的一页 + 该选择成立时的桥世代。
///
/// 刻意不包含：调用者身份、任务/父任务、读写分级、过期时间、租约、持有者。
/// 理由见方案 §9.2/§9.3：这些维度在本地单用户单机场景下没有形成闭环的可验证收益，
/// 却把「一次调用」拆成多道 fail-closed 闸门。身份只用于审计（另有组件负责）。
/// </summary>
public sealed record AgentTargetSession
{
    public AgentTargetSession(
        AgentTargetSessionId sessionId,
        DesktopPageTarget target,
        ConnectionGeneration bridgeGeneration,
        DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!bridgeGeneration.IsLive)
        {
            throw new ArgumentException(
                "a target session must be bound to a live bridge generation; a dead generation could not be validated.",
                nameof(bridgeGeneration));
        }

        SessionId = sessionId;
        Target = target;
        BridgeGeneration = bridgeGeneration;
        StartedAtUtc = startedAtUtc;
    }

    public AgentTargetSessionId SessionId { get; }

    public DesktopPageTarget Target { get; }

    /// <summary>选择成真时的桥世代。桥换代 ⇒ 本会话作废（见 <see cref="AgentTargetDenial.TransportUnavailable"/>）。</summary>
    public ConnectionGeneration BridgeGeneration { get; }

    public DateTimeOffset StartedAtUtc { get; }
}

/// <summary>控制面快照。UI/诊断只读它，不得各自维护一份状态（历史上三份漂移过）。</summary>
public sealed record AgentTargetSessionSnapshot(
    bool IsBridgeReady,
    ConnectionGeneration BridgeGeneration,
    BrowserControlEpoch ControlEpoch,
    AgentTargetSession? Session,
    bool IsUserControlling,
    bool IsClosed,
    AgentTargetSessionId? LastRevokedSessionId,
    string? Reason)
{
    public bool HasTarget => Session is not null;

    /// <summary>给 UI 的一行状态：连接态 + 目标态，避免用户点了按钮却看不到任何变化。</summary>
    public string Describe()
    {
        var bridge = IsBridgeReady
            ? string.Concat("Connected(gen=", BridgeGeneration.Value.ToString(CultureInfo.InvariantCulture), ")")
            : "Disconnected";
        var target = Session is null ? "NoTarget" : string.Concat("Target=", Session.Target.Key);
        var control = IsClosed ? "Closed" : (IsUserControlling ? "UserControlling" : "AgentControllable");
        return string.Concat(bridge, " · ", target, " · ", control);
    }
}
