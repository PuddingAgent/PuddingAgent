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

/// <summary>迁移使用统计：把"能否退役旧 Bridge"变成可判定谓词（保守：零回退 + 通道已证明在用）。</summary>
public sealed class TransportUsageTests
{
    [Fact]
    public void FreshWindow_CannotRetireBecauseTheChannelIsUnproven()
    {
        var usage = DesktopTransportUsage.Empty;

        Assert.False(usage.ChannelProven);
        Assert.False(usage.CanRetireLegacyBridge);
        Assert.Contains("尚未观测到任何成功使用", usage.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelOnly_ClearsTheWayForRetirement()
    {
        var usage = DesktopTransportUsage.Empty
            .Record(DesktopTransportRoute.CapabilityChannel)
            .Record(DesktopTransportRoute.CapabilityChannel);

        Assert.True(usage.ChannelProven);
        Assert.True(usage.CanRetireLegacyBridge);
        Assert.Contains("可退役", usage.Explain(), StringComparison.Ordinal);
        Assert.Contains("channel=2", usage.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnyFallbackOrDeadEnd_BlocksRetirement()
    {
        var withFallback = DesktopTransportUsage.Empty
            .Record(DesktopTransportRoute.CapabilityChannel)
            .Record(DesktopTransportRoute.LegacyBridge);

        Assert.False(withFallback.CanRetireLegacyBridge);
        Assert.Contains("回退到旧 Bridge", withFallback.Explain(), StringComparison.Ordinal);

        var withDeadEnd = DesktopTransportUsage.Empty
            .Record(DesktopTransportRoute.CapabilityChannel)
            .Record(DesktopTransportRoute.None);

        Assert.False(withDeadEnd.CanRetireLegacyBridge);
        Assert.Contains("无路可走", withDeadEnd.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void Counters_MustBeNonNegativeAndExplanationsStayContentFree()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopTransportUsage(-1, 0, 0));

        var usage = DesktopTransportUsage.Empty.Record(DesktopTransportRoute.LegacyBridge);
        Assert.DoesNotContain("http", usage.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", usage.Explain(), StringComparison.OrdinalIgnoreCase);
    }
}
}
