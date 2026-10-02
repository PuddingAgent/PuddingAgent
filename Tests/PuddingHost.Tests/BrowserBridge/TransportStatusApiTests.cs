using Microsoft.Extensions.DependencyInjection;
using Pudding.CapabilityBroker;
using PuddingHost.BrowserBridge;
using PuddingHost.Controllers;
using Xunit;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// 切片 F 的**退役判据证据面**：<c>GET /api/admin/transport/status</c>。
///
/// 直接对控制器取快照（不起 WebHost、不走 JWT）——鉴权与路由由 <c>[Authorize]</c>/<c>[Route]</c>
/// 声明，与 <c>IndexAdminController</c> 逐项一致；本测试钉住的是**字段语义**：
/// 计数器未注册时必须如实说 <c>available = false</c>，而不是编造 0 或 500。
/// </summary>
public sealed class TransportStatusApiTests
{
    [Fact]
    public void Status_ReportsUnavailable_WhenBrowserAutomationIsDisabled()
    {
        // 浏览器自动化关闭 ⇒ 窄端口与计数器都不注册（组合根的条件注册）。
        using var provider = new ServiceCollection().BuildServiceProvider();
        var controller = new TransportStatusController(provider);

        var snapshot = controller.GetStatus().Value;

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.Available);
        Assert.False(snapshot.ChannelProven);
        Assert.False(snapshot.CanRetireLegacyBridge);
        Assert.Equal(0, snapshot.CapabilityChannelCalls);
    }

    [Fact]
    public void Status_TracksChannelAndLegacyCalls_AndFlipsTheRetirementCriterion()
    {
        var tracker = new DesktopTransportUsageTracker();
        using var provider = new ServiceCollection()
            .AddSingleton(tracker)
            .BuildServiceProvider();
        var controller = new TransportStatusController(provider);

        // 尚未调用：通道未被证明 ⇒ 不可退役。
        var fresh = controller.GetStatus().Value!;
        Assert.True(fresh.Available);
        Assert.False(fresh.ChannelProven);
        Assert.False(fresh.CanRetireLegacyBridge);

        // 落地真实调用记录（与本文件同一程序集，故可直连内部计数器）。
        tracker.Record(DesktopTransportRoute.CapabilityChannel);
        tracker.Record(DesktopTransportRoute.CapabilityChannel);
        tracker.Record(DesktopTransportRoute.LegacyBridge);

        var afterBridgeFallback = controller.GetStatus().Value!;
        Assert.Equal(2, afterBridgeFallback.CapabilityChannelCalls);
        Assert.Equal(1, afterBridgeFallback.LegacyBridgeCalls);
        Assert.True(afterBridgeFallback.ChannelProven);
        // 窗口内出现过一次回退 ⇒ **不可**退役（判据是零回退 + 零无路由）。
        Assert.False(afterBridgeFallback.CanRetireLegacyBridge);

        // 再走 10 次通道、且此后零回退：判据仍要求窗口内 legacy = 0，因此这里如实保持 false；
        // 「可退役」只在**从零开始**的清洁窗口里成立 —— 这正是需要一个干净验收窗口的原因。
        for (var i = 0; i < 10; i++)
        {
            tracker.Record(DesktopTransportRoute.CapabilityChannel);
        }

        var dirtyWindow = controller.GetStatus().Value!;
        Assert.Equal(12, dirtyWindow.CapabilityChannelCalls);
        Assert.False(dirtyWindow.CanRetireLegacyBridge);
    }

    [Fact]
    public void Status_ReportsRetirable_OnACleanChannelOnlyRecord()
    {
        var tracker = new DesktopTransportUsageTracker();
        using var provider = new ServiceCollection()
            .AddSingleton(tracker)
            .BuildServiceProvider();

        tracker.Record(DesktopTransportRoute.CapabilityChannel);
        tracker.Record(DesktopTransportRoute.CapabilityChannel);

        var snapshot = new TransportStatusController(provider).GetStatus().Value!;

        Assert.True(snapshot.Available);
        Assert.True(snapshot.ChannelProven);
        Assert.Equal(0, snapshot.LegacyBridgeCalls);
        Assert.Equal(0, snapshot.NoRouteCalls);
        Assert.True(snapshot.CanRetireLegacyBridge);
    }
}
