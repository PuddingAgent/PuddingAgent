using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Controllers;

/// <summary>
/// 传输用量**只读出口**（技术方案 §5 切片 F 的退役判据证据面）。
///
/// <para>
/// 为什么需要它：能力通道与既有 WebSocket Bridge 会在组合根**二选一**（<c>DesktopTransportRouting.Decide</c>），
/// 「可以退役旧 Bridge」的判据是「窗口内能力通道调用 &gt; 0 且旧 Bridge 回退 = 0」
/// （<see cref="DesktopTransportUsage.CanRetireLegacyBridge"/>）。此前该计数只存在于进程内，
/// 外部验收只能从人读日志里找 ⇒ 本出口把它变成机器可读的状态字段。
/// </para>
/// <para>
/// 鉴权与命名空间照抄 <see cref="IndexAdminController"/>：同样只认登录态 Admin JWT（<c>Roles = "admin"</c>），
/// 不接 Desktop ControlToken、不接 External Access Token —— 「看传输用量」与「看索引/存储状态」是同级的运维面。
/// </para>
/// <para>
/// 纯只读、零副作用：只读一个内存计数器的快照，不触发任何能力调用、不改变路由。
/// 浏览器自动化未启用时该计数器**未注册** ⇒ 如实返回 <c>available = false</c>（而不是 500 或编造 0）。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/transport")]
[Authorize(
    AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
    Roles = "admin")]
public sealed class TransportStatusController(IServiceProvider services) : ControllerBase
{
    /// <summary>传输用量快照；见 <see cref="DesktopTransportStatusSnapshot"/> 的字段说明。</summary>
    [HttpGet("status")]
    [ProducesResponseType<DesktopTransportStatusSnapshot>(StatusCodes.Status200OK)]
    public ActionResult<DesktopTransportStatusSnapshot> GetStatus()
    {
        // 浏览器自动化关闭时窄端口/计数器都不注册 ⇒ GetService 返回 null（不注入构造函数，避免整个控制器构造失败）。
        var usage = services.GetService<DesktopTransportUsageTracker>()?.Current;
        return new DesktopTransportStatusSnapshot(
            Available: usage is not null,
            CapabilityChannelCalls: usage?.CapabilityChannelCalls ?? 0,
            LegacyBridgeCalls: usage?.LegacyBridgeCalls ?? 0,
            NoRouteCalls: usage?.NoRouteCalls ?? 0,
            ChannelProven: usage?.ChannelProven ?? false,
            CanRetireLegacyBridge: usage?.CanRetireLegacyBridge ?? false);
    }
}

/// <summary>
/// 传输用量快照。字段名是**外部验收的证据字段**（运行手册第 6 步），一旦发布不得随意改名。
/// </summary>
/// <param name="Available">
/// 计数器是否已注册（= 浏览器自动化是否启用）。<c>false</c> 时其余计数一律为 0，
/// 且这些 0 **不代表**「没有调用」——因此必须同时看这个字段。
/// </param>
/// <param name="CapabilityChannelCalls">经能力通道执行的调用数（迁移目标传输）。</param>
/// <param name="LegacyBridgeCalls">回退到既有 WebSocket Bridge 的调用数。</param>
/// <param name="NoRouteCalls">两条传输都不可用而**如实失败**的调用数（绝不跨传输重试）。</param>
/// <param name="ChannelProven">能力通道是否已被真实调用过（<c>CapabilityChannelCalls &gt; 0</c>）。</param>
/// <param name="CanRetireLegacyBridge">
/// 退役判据：<see cref="ChannelProven"/> 且 <see cref="LegacyBridgeCalls"/> = 0 且 <see cref="NoRouteCalls"/> = 0。
/// </param>
public sealed record DesktopTransportStatusSnapshot(
    bool Available,
    long CapabilityChannelCalls,
    long LegacyBridgeCalls,
    long NoRouteCalls,
    bool ChannelProven,
    bool CanRetireLegacyBridge);
