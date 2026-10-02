using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>组合根工厂：关闭 ⇒ 不构造；启用 ⇒ 构造但**不启动**（启动是组合根的生命周期决定）。</summary>
public sealed class DesktopCapabilityHostFactoryTests
{
    private static readonly DesktopProcessInstanceId Process = new("desktop-process-1");

    [Fact]
    public void DisabledSettingsDoNotProduceAHost()
    {
        var result = DesktopCapabilityHostFactory.Create(
            DesktopCapabilityChannelSettings.Disabled, Process, null, new FakeStreamFactory(), new FakeExecutor());

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public void EnabledSettingsProduceAStoppedHost()
    {
        var result = DesktopCapabilityHostFactory.Create(
            new DesktopCapabilityChannelSettings { Enabled = true }, Process, null,
            new FakeStreamFactory(), new FakeExecutor());

        Assert.False(result.IsFailure);
        // 只构造、不启动：启动会占用「同一 DesktopId 单一活动传输」的名额。
        Assert.False(result.Value.IsRunning);
        Assert.Equal(DesktopConnectionState.Idle, result.Value.State);

        result.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void UnusableHandshakeTimeoutBecomesAnErrorInsteadOfAnException()
    {
        var result = DesktopCapabilityHostFactory.Create(
            new DesktopCapabilityChannelSettings { Enabled = true, HandshakeTimeoutSeconds = 0 }, Process, null,
            new FakeStreamFactory(), new FakeExecutor());

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public void InvalidDesktopIdIsReportedAsConfigurationError()
    {
        var result = DesktopCapabilityHostFactory.Create(
            new DesktopCapabilityChannelSettings { Enabled = true, DesktopId = "  " }, Process, null,
            new FakeStreamFactory(), new FakeExecutor());

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, result.Error!.Code);
    }

    private sealed class FakeStreamFactory : IDesktopChannelStreamFactory
    {
        // 真签名：ValueTask<DesktopChannelStream> OpenAsync(CancellationToken)，且传的是
        // **抽象流**（不是泛型结果）⇒ 用异常表达"打不开"，宿主会把它折叠为监督器失败。
        public ValueTask<DesktopChannelStream> OpenAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("fake stream factory never opens");
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