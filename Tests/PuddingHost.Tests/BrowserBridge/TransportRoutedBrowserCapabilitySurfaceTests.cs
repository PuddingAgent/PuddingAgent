using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// 组合根按传输决策二选一：通道就绪走通道、否则走 Bridge、两条都没有就**如实失败**。
/// 唯一的硬要求（也是这组测试的焦点）：**同一次操作绝不执行两次**——选了通道就绝不再碰 Bridge。
/// </summary>
public sealed class TransportRoutedBrowserCapabilitySurfaceTests
{
    private static readonly DesktopCallContext Call = new(
        new DesktopInstanceId("desk-1"), new OperationId("op-1"), DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task ChannelReady_PrefersTheChannelAndNeverTouchesTheBridge()
    {
        var channel = new RecordingSurface("channel");
        var bridge = new RecordingSurface("bridge");
        var usage = new DesktopTransportUsageTracker();
        var surface = Create(channel, bridge, usage);

        var result = await surface.GetContextsAsync(Call);

        Assert.False(result.IsFailure);
        Assert.Equal("channel", result.Value.Contexts[0].ContextId);
        Assert.Equal(1, channel.Invocations);
        Assert.Equal(0, bridge.Invocations);
        Assert.Equal(1, usage.Current.CapabilityChannelCalls);
        Assert.Equal(0, usage.Current.LegacyBridgeCalls);
    }

    [Fact]
    public async Task WithoutChannel_UsesTheLegacyBridgeAndCountsIt()
    {
        var bridge = new RecordingSurface("bridge");
        var usage = new DesktopTransportUsageTracker();
        var surface = Create(channel: null, bridge, usage);

        var result = await surface.GetContextsAsync(Call);

        Assert.False(result.IsFailure);
        Assert.Equal("bridge", result.Value.Contexts[0].ContextId);
        Assert.Equal(1, bridge.Invocations);
        Assert.Equal(1, usage.Current.LegacyBridgeCalls);
        Assert.False(usage.Current.ChannelProven);
    }

    [Fact]
    public async Task WithNeither_ReportsNotConnectedInsteadOfTryingSomethingElse()
    {
        // 两条都不可用 ⇒ 明确失败；**不得**换一条重试（那会把操作再做一遍）。
        var bridge = new RecordingSurface("bridge");
        var usage = new DesktopTransportUsageTracker();
        var surface = new TransportRoutedBrowserCapabilitySurface(
            bridge,
            activeChannel: () => null,
            legacyBridgeAvailable: () => false,
            usage);

        var result = await surface.GetContextsAsync(Call);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, result.Error!.Code);
        Assert.Equal(0, bridge.Invocations);
        Assert.Equal(1, usage.Current.NoRouteCalls);
    }

    private static TransportRoutedBrowserCapabilitySurface Create(
        IDesktopBrowserCapabilitySurface? channel,
        IDesktopBrowserCapabilitySurface bridge,
        DesktopTransportUsageTracker usage) =>
        new(bridge, activeChannel: () => channel, legacyBridgeAvailable: () => true, usage);

    private sealed class RecordingSurface(string contextId) : IDesktopBrowserCapabilitySurface
    {
        public int Invocations { get; private set; }

        public Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
            DesktopCallContext call, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.FromResult(CapabilityResult<DesktopContexts>.Success(
                new DesktopContexts([new DesktopContextInfo(contextId, DesktopContextTrust.AgentAuthorized, [])])));
        }

        public Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
            DesktopPageTarget target, DesktopCallContext call, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
            BrowserTabsRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapabilityResult<NavigateResult>> NavigateAsync(
            NavigateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
            BrowserSnapshotRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
            BrowserLocateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
            BrowserInteractRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
            BrowserWaitForRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
            JavascriptRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
