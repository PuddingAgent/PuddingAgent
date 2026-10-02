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

    private const string GoodDescription = "named-pipe:pudding-capability-abc|1|core-42";

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