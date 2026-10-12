using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 组合根将要使用的**确切顺序**：先 preflight 判定，再决定是否构造宿主。
/// 顺序错了会踩到规格 §7.2 的陷阱（`StartAsync` 在旧传输模式下抛异常），因此在这里把它钉住。
/// </summary>
public sealed class DesktopCapabilityCompositionTests
{
    private static readonly DesktopProcessInstanceId Process = new("desktop-process-1");

    private static readonly string GoodDescription = $"named-pipe:pudding-capability-abc|{DesktopProtocolVersion.Current}|core-42";

    /// <summary>
    /// §5.4：组合根**只接受生命周期端口** —— 它同时持有唯一权威与准入读的页面注册表。
    /// 允许拆成两个参数就允许"配错对"（注册表来自 A、权威来自 B）⇒ 又出现**两份控制权状态**。
    /// 这条用例把该契约钉住：缺生命周期必须**明确拒绝**，而不是悄悄各建一份。
    /// </summary>
    /// <summary>
    /// §5.4：**关闭态是正常路径** —— 不构造、不启动，把控制权完整留给既有 Bridge；
    /// 因此这一步**不得**产生第二份控制权状态（同一 lifecycle 的 `Authority` 必须保持**同一实例**）。
    /// </summary>
    /// <summary>
    /// §5.4：**唯一权威由外部注入、被共享，绝不被复制** —— 组合根把同一个 `lifecycle.Authority`
    /// 同时交给能力通道与既有 Bridge 路径。若服务自己新建一份权威，两条传输就会各有一套控制权状态。
    /// 这条用例钉住"注入即共享"：用同一个 lifecycle 建两份服务（模拟两条传输各一份）⇒ 权威仍是同一实例。
    /// </summary>
    [Fact]
    public void TwoServicesBuiltFromTheSameLifecycleShareOneAuthority()
    {
        var clock = new TestTimeProvider();
        var lifecycle = BrowserPageLifecycle.Create(
            new DesktopInstanceId("desk-share"), new DesktopProcessInstanceId("proc-share"), clock);

        DesktopService Build() => new(
            new ManualUiDispatcher { HasThreadAccess = true },
            new RecordingUiSurface(),
            lifecycle.PageTargets,
            new DesktopServiceOptions
            {
                AllowedCapabilities = DesktopCapability.BrowserBindingApply | DesktopCapability.BrowserBindingRevoke,
                BrowserContextCallerTrust = DesktopContextTrust.AgentAuthorized,
            },
            lifecycle.Authority,
            clock,
            () => ConnectionGeneration.Require(4),
            taskBindings: null);

        var bridgeSide = Build();
        var channelSide = Build();

        Assert.Same(lifecycle.Authority, bridgeSide.Authority);
        Assert.Same(bridgeSide.Authority, channelSide.Authority);
    }
    [Fact]
    public async Task DisabledChannelLeavesTheSingleAuthorityUntouched()
    {
        var lifecycle = BrowserPageLifecycle.Create(
            new DesktopInstanceId("desk-1"), Process, TimeProvider.System);
        var before = lifecycle.Authority;

        var result = await DesktopCapabilityChannelComposition.StartAsync(
            DesktopCapabilityChannelSettings.Disabled,
            GoodDescription,
            authentication: null,
            Process,
            new ManualUiDispatcher(),
            new RecordingUiSurface(),
            lifecycle);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Same(before, lifecycle.Authority);
    }
    [Fact]
    public async Task TheChannelCompositionRefusesToStartWithoutTheLifecyclePort()
    {
        var settings = new DesktopCapabilityChannelSettings { Enabled = true };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DesktopCapabilityChannelComposition.StartAsync(
                settings,
                GoodDescription,
                authentication: null,
                Process,
                new ManualUiDispatcher(),
                new RecordingUiSurface(),
                lifecycle: null!));
    }
    [Fact]
    public void DisabledChannel_KeepsTheLegacyBridgeAndNeverBuildsAHost()
    {
        var settings = DesktopCapabilityChannelSettings.Disabled;

        var preflight = DesktopCapabilityChannelPreflight.Evaluate(settings, Process, null, GoodDescription);
        Assert.False(preflight.ShouldStart);

        // 组合根必须按 ShouldStart 短路：不构造宿主。
        var host = DesktopCapabilityHostFactory.Create(settings, Process, null, new FakeStreamFactory(), new FakeExecutor());
        Assert.True(host.IsFailure);
    }

    [Fact]
    public void DisabledChannelWithNoDescription_StillKeepsTheLegacyBridge()
    {
        var preflight = DesktopCapabilityChannelPreflight.Evaluate(
            DesktopCapabilityChannelSettings.Disabled, Process, null, endpointDescription: null);

        Assert.False(preflight.ShouldStart);
        Assert.Contains("Enabled is false", preflight.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledChannelWithAGoodDescription_PassesBothGatesAndYieldsAStoppedHost()
    {
        var settings = new DesktopCapabilityChannelSettings { Enabled = true };

        var preflight = DesktopCapabilityChannelPreflight.Evaluate(settings, Process, null, GoodDescription);
        Assert.True(preflight.ShouldStart);

        var host = DesktopCapabilityHostFactory.Create(settings, Process, null, new FakeStreamFactory(), new FakeExecutor());
        Assert.False(host.IsFailure);
        // 构造 ≠ 启动：启动名额由组合根在确认要启用时才占用。
        Assert.False(host.Value.IsRunning);

        host.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void EnabledChannelWithoutAUsableDescription_StopsBeforeBuildingAHost()
    {
        var settings = new DesktopCapabilityChannelSettings { Enabled = true };

        var preflight = DesktopCapabilityChannelPreflight.Evaluate(
            settings, Process, null, endpointDescription: "not-a-description");
        Assert.False(preflight.ShouldStart);
        Assert.Contains("missing or not usable", preflight.Summary, StringComparison.Ordinal);
    }

    private sealed class FakeStreamFactory : IDesktopChannelStreamFactory
    {
        public ValueTask<DesktopChannelStream> OpenAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("composition test never opens a stream");
    }

    private sealed class FakeExecutor : IDesktopCapabilityExecutor
    {
        public Task<DesktopCapabilityResponse> ExecuteAsync(
            DesktopCapabilityDescriptor capability,
            DesktopCapabilityRequest request,
            DesktopCallContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DesktopCapabilityResponse.Failure(DesktopCapabilityError.Internal("fake executor")));
    }
}