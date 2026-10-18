using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Authorization;

namespace Pudding.DesktopService;

/// <summary>
/// 稳定拒绝码（与 <c>PuddingBrowser.Authorization.AgentTargetErrorCodes</c> 逐字一致）。
/// 单独再声明一次是因为 Shell/桥侧程序集**不得引用 Automation/Authorization 组件**，而拒绝码要过线缆。
/// </summary>
public static class BrowserSessionErrorCodes
{
    public const string TransportUnavailable = "browser_transport_unavailable";
    public const string TargetRequired = "agent_target_required";
    public const string TargetRevoked = "agent_target_revoked";
    public const string UserConfirmationRequired = "user_confirmation_required";
    public const string Busy = "browser_busy";
    public const string Closed = "browser_closed";
    public const string ControlHandleRequired = "browser_control_handle_required";
    public const string OwnerMismatch = "browser_owner_mismatch";
    public const string PermitInvalid = "browser_permit_invalid";
}

/// <summary>
/// 宿主注入的**可信控制句柄**（只含标量）：一次桥命令所属的 Core 会话所有者。
/// 桥侧从命令的可信 Origin（Host 注入的 ConversationId/SessionId）得到，不由模型参数决定。
/// </summary>
public readonly record struct BrowserSessionControlHandle(string OwnerId)
{
    public const int MaxLength = 128;

    public static BrowserSessionControlHandle Require(string? ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > MaxLength)
        {
            throw new ArgumentException("owner id must be a non-empty identifier", nameof(ownerId));
        }

        return new BrowserSessionControlHandle(ownerId);
    }
}

/// <summary>
/// 一张已准入的页面命令票据（**不透明**）：桥侧只负责在 finally 里原样交回，
/// 复检/释放的判据全部在会话权威内部（唯一票据、写世代、取消信号）。
/// </summary>
public sealed class BrowserSessionPermit
{
    internal BrowserSessionPermit(AgentTargetPermit permit, string ownerId)
    {
        Permit = permit;
        OwnerId = ownerId;
    }

    internal AgentTargetPermit Permit { get; }

    public string OwnerId { get; }
}

/// <summary>会话准入判定结果。</summary>
public sealed record BrowserSessionDecision(
    bool Allow,
    string? ErrorCode,
    string? Message,
    BrowserSessionPermit? Permit)
{
    public static BrowserSessionDecision Allowed(BrowserSessionPermit permit) =>
        new(true, null, null, permit);

    public static BrowserSessionDecision Denied(string errorCode, string message) =>
        new(false, errorCode, message, null);
}

/// <summary>控制面快照（标量，供 UI/诊断/桥侧读；不含 Authorization 类型）。</summary>
public sealed record BrowserSessionStatus(
    bool IsBridgeReady,
    bool HasTarget,
    bool HasPendingHandover,
    string? Target,
    string? PendingTarget,
    bool IsPaused,
    bool IsUserControlling,
    bool IsClosed,
    string? Reason)
{
    /// <summary>UI 一行状态：交页后必须能区分“待确认/已交出/无目标”。</summary>
    public string Describe()
    {
        var target = HasTarget
            ? string.Concat("Target=", Target)
            : HasPendingHandover
                ? string.Concat("PendingHandover=", PendingTarget)
                : "NoTarget";
        var control = IsClosed ? "Closed" : IsUserControlling ? "UserControlling" : IsPaused ? "Paused" : "AgentControllable";
        return string.Concat(IsBridgeReady ? "Connected" : "Disconnected", " · ", target, " · ", control);
    }
}
