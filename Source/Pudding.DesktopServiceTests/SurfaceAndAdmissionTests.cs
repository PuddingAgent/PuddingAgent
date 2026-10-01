using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace DesktopServiceTests;

/// <summary>表面返回值的透传与异常折叠，以及只读直连 API 的调度路径。</summary>
public sealed class SurfaceAndAdmissionTests
{
    [Fact]
    public async Task SurfaceDomainFailure_PassesThroughUnchanged()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        var expected = DesktopCapabilityError.Unauthorized("page requires re-authorization");
        harness.Surface.NavigateHandler = (_, _, _) => Task.FromResult(CapabilityResult<NavigateResult>.Failure(expected));

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, response.Error!.Code);
        Assert.Equal(expected.Message, response.Error.Message);
    }

    [Fact]
    public async Task SurfaceException_MapsToInternalErrorWithoutLeakingItsMessage()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        harness.Surface.NavigateHandler = (_, _, _) =>
            throw new InvalidOperationException("boom: page title was 机密标题");

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.InternalError, response.Error!.Code);
        Assert.DoesNotContain("boom", response.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("机密", response.Error.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", response.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPageState_RoutesThroughTheUiDispatcher()
    {
        var harness = ServiceHarness.Create();

        var pending = harness.Service.GetPageStateAsync(harness.Context(), ServiceHarness.AgentPage);
        Assert.Equal(1, harness.Dispatcher.QueuedCount);

        harness.Dispatcher.Pump();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal(DesktopPageReadiness.Complete, result.Value.Readiness);
        Assert.Equal(1, harness.Surface.PageStateCount);
    }

    [Fact]
    public async Task GetPageState_OnUnknownTarget_IsRejected()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var result = await harness.Service.GetPageStateAsync(
            harness.Context(), new DesktopPageTarget("ctx-agent", "missing-page"));

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, result.Error.Code);
        Assert.Equal(0, harness.Surface.PageStateCount);
    }

    [Fact]
    public async Task GetPageState_IsAllowedOnUntrustedPages()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var result = await harness.Service.GetPageStateAsync(harness.Context(), ServiceHarness.WebPage);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task GetPageState_RespectsTheOperationDeadline()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var result = await harness.Service.GetPageStateAsync(
            harness.Context(deadline: TimeSpan.FromSeconds(-1)), ServiceHarness.AgentPage);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.DeadlineExceeded, result.Error.Code);
        Assert.Equal(0, harness.Surface.PageStateCount);
    }

    [Fact]
    public async Task Notification_IsAllowedForUntrustedCallerAndKeepsItsResult()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.ShellNotification, harness.NotificationRequest());

        Assert.True(response.Notification!.Shown);
        Assert.Equal("n-1", response.Notification.NotificationId);
        Assert.Equal(1, harness.Surface.NotificationCount);
    }

    [Fact]
    public async Task InteractiveCapabilities_StayDisabledUntilTheirPayloadExists()
    {
        // 默认 ShellCallerTrust = Untrusted ⇒ 对话框/Picker/剪贴板不可用（切片 E 逐能力开放）。
        var harness = ServiceHarness.Create(
            allowed: DesktopCapability.ShellDialog | DesktopCapability.ShellNotification,
            hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.ShellDialog, harness.NotificationRequest());

        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, response.Error!.Code);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsNullArguments()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        var descriptor = ServiceHarness.Descriptor(DesktopCapability.WebViewNavigate);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ExecuteAsync(null!, harness.NavigateRequest(ServiceHarness.AgentPage), harness.Context()));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ExecuteAsync(descriptor, null!, harness.Context()));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ExecuteAsync(descriptor, harness.NavigateRequest(ServiceHarness.AgentPage), null!));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsRequestThatDoesNotCarryItsPayload()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NotificationRequest());

        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task DisposeAsync_ClosesTheService()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        await harness.Service.DisposeAsync();

        Assert.True(harness.Service.IsClosed);
    }

    [Fact]
    public async Task PageStateCommand_ReturnsPageStateThroughTheDispatcher()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewPageState,
            DesktopCapabilityRequest.ForPageState(ServiceHarness.AgentPage));

        Assert.NotNull(response.PageState);
        Assert.Equal(DesktopPageReadiness.Complete, response.PageState.Readiness);
        Assert.Equal(1, harness.Surface.PageStateCount);
    }

    [Fact]
    public async Task PageStateCommand_WithoutItsPayload_IsRejectedAsInvalidRequest()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewPageState, harness.NotificationRequest());

        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, response.Error!.Code);
        Assert.Equal(0, harness.Surface.PageStateCount);
    }
}
