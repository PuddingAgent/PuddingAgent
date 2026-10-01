using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace CapabilityBrokerTests;

/// <summary>
/// 迁移期传输选择：唯一的安全要求是「同一次操作绝不执行两次」。
/// 因此一旦本次操作尝试过能力通道，就绝不允许回退到旧 Bridge。
/// </summary>
public sealed class TransportRoutingTests
{
    [Fact]
    public void ReadyChannel_WinsDuringMigration()
    {
        var decision = DesktopTransportRouting.Decide(
            channelReady: true, channelAttempted: false, legacyBridgeAvailable: true);

        Assert.Equal(DesktopTransportRoute.CapabilityChannel, decision.Route);
        Assert.True(decision.IsRoutable);
    }

    [Fact]
    public void ChannelNotReadyYet_FallsBackToTheLegacyBridge()
    {
        // 迁移期允许回退，但前提是本次操作**尚未**碰过通道。
        var decision = DesktopTransportRouting.Decide(
            channelReady: false, channelAttempted: false, legacyBridgeAvailable: true);

        Assert.Equal(DesktopTransportRoute.LegacyBridge, decision.Route);
    }

    [Fact]
    public void AfterTheChannelWasAttempted_ThereIsNoFallback()
    {
        // 最关键的一条：通道超时不代表 Desktop 没执行，回退会把它再做一遍。
        var decision = DesktopTransportRouting.Decide(
            channelReady: false, channelAttempted: true, legacyBridgeAvailable: true);

        Assert.Equal(DesktopTransportRoute.None, decision.Route);
        Assert.False(decision.IsRoutable);
        Assert.Contains("不得回退", decision.Reason, StringComparison.Ordinal);

        var error = DesktopTransportRouting.NoRoute(decision);
        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, error.Code);
    }

    [Fact]
    public void AttemptedChannelThatIsStillReady_KeepsWaitingOnIt()
    {
        var decision = DesktopTransportRouting.Decide(
            channelReady: true, channelAttempted: true, legacyBridgeAvailable: true);

        Assert.Equal(DesktopTransportRoute.CapabilityChannel, decision.Route);
    }

    [Fact]
    public void NothingAvailable_IsReportedAsFailureNotAsSilentRetry()
    {
        var decision = DesktopTransportRouting.Decide(
            channelReady: false, channelAttempted: false, legacyBridgeAvailable: false);

        Assert.Equal(DesktopTransportRoute.None, decision.Route);
        Assert.Equal(
            DesktopCapabilityErrorCode.NotConnected,
            DesktopTransportRouting.NoRoute(decision).Code);

        // 只有「无路可走」才能被当成错误上报；有路可走时误用是调用方的 bug。
        var routable = DesktopTransportRouting.Decide(true, false, false);
        Assert.Throws<ArgumentException>(() => DesktopTransportRouting.NoRoute(routable));
    }

    [Fact]
    public void Reasons_NeverContainPageContentOrCredentials()
    {
        foreach (var decision in new[]
                 {
                     DesktopTransportRouting.Decide(true, false, false),
                     DesktopTransportRouting.Decide(false, false, true),
                     DesktopTransportRouting.Decide(false, true, true),
                     DesktopTransportRouting.Decide(false, false, false),
                 })
        {
            Assert.NotEmpty(decision.Reason);
            Assert.DoesNotContain("http", decision.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }
}
