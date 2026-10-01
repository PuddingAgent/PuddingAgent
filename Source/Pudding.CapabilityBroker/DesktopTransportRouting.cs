using Pudding.Contracts;

namespace Pudding.CapabilityBroker;

/// <summary>迁移期一次操作应该走哪条传输。</summary>
public enum DesktopTransportRoute
{
    /// <summary>走能力通道（Desktop 主动建立的双向流）。</summary>
    CapabilityChannel,

    /// <summary>走既有 WebSocket Bridge（迁移期未就绪时的旧路径）。</summary>
    LegacyBridge,

    /// <summary>两条都不可用：调用方必须如实报告失败，**不得**自行换一条重试。</summary>
    None,
}

/// <summary>选择结果 + 理由（理由用于审计与日志，不含页面内容）。</summary>
public sealed record DesktopTransportDecision(DesktopTransportRoute Route, string Reason)
{
    public bool IsRoutable => Route != DesktopTransportRoute.None;
}

/// <summary>
/// 迁移期的传输选择规则（计划 §5「普通业务保留 HTTP 与既有事件通道，分阶段迁移现有 WebSocket Bridge」）。
///
/// 唯一的安全要求是**同一次操作绝不执行两次**：因此一旦本次操作已经尝试过能力通道，
/// 就绝不允许再回退到旧 Bridge —— 这正是"跨传输回退"最危险的地方
/// （通道超时不代表 Desktop 没执行，回退会把它再做一遍）。
///
/// 规则（fail closed）：
/// ① 已尝试过能力通道 ⇒ 只允许继续等通道结果；通道不可用则 <see cref="DesktopTransportRoute.None"/>；
/// ② 通道就绪 ⇒ 走通道；
/// ③ 否则若旧 Bridge 可用 ⇒ 走旧 Bridge（此时本次操作**尚未**碰过通道，重复执行风险不存在）；
/// ④ 否则 <see cref="DesktopTransportRoute.None"/>。
/// </summary>
public static class DesktopTransportRouting
{
    public static DesktopTransportDecision Decide(
        bool channelReady,
        bool channelAttempted,
        bool legacyBridgeAvailable)
    {
        if (channelAttempted)
        {
            return channelReady
                ? new DesktopTransportDecision(
                    DesktopTransportRoute.CapabilityChannel,
                    "本次操作已尝试过能力通道，只能等通道结果（不得回退，避免重复执行）")
                : new DesktopTransportDecision(
                    DesktopTransportRoute.None,
                    "本次操作已尝试过能力通道但通道已不可用：不得回退到旧 Bridge（避免重复执行），应如实报告失败");
        }

        if (channelReady)
        {
            return new DesktopTransportDecision(
                DesktopTransportRoute.CapabilityChannel, "能力通道就绪，优先使用（迁移目标传输）");
        }

        if (legacyBridgeAvailable)
        {
            return new DesktopTransportDecision(
                DesktopTransportRoute.LegacyBridge, "能力通道未就绪，且本次操作尚未使用通道：使用既有 WebSocket Bridge");
        }

        return new DesktopTransportDecision(
            DesktopTransportRoute.None, "能力通道未就绪且旧 Bridge 不可用：无法路由，如实报告失败");
    }

    /// <summary>
    /// 把选择结果包成一个明确失败（供调用方在 <see cref="DesktopTransportRoute.None"/> 时使用）——
    /// 错误文案统一，避免各处自造"暂时不可用"之类含糊说法。
    /// </summary>
    public static DesktopCapabilityError NoRoute(DesktopTransportDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return decision.Route == DesktopTransportRoute.None
            ? DesktopCapabilityError.NotConnected(decision.Reason)
            : throw new ArgumentException("Only an unroutable decision can be reported as an error.", nameof(decision));
    }
}
