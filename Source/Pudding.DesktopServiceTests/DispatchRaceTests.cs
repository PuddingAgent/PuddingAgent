using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace DesktopServiceTests;

/// <summary>
/// UI 调度边界与「入队后竞态」：排队期间页面关闭/版本推进/窗口退出/期限过期都必须得到明确终态，
/// 而且不允许调用到 UI 表面。
/// </summary>
public sealed class DispatchRaceTests
{
    [Fact]
    public async Task ThreadAccess_RunsInlineWithoutQueueing()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.NotNull(response.Navigate);
        Assert.Equal(0, harness.Dispatcher.QueuedCount);
    }

    [Fact]
    public async Task BusyUiThread_QueuesThenRunsOnPump()
    {
        var harness = ServiceHarness.Create();

        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(1, harness.Dispatcher.QueuedCount);
        Assert.False(pending.IsCompleted);

        Assert.Equal(1, harness.Dispatcher.Pump());

        var response = await pending;
        Assert.NotNull(response.Navigate);
        Assert.Equal(1, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task PageClosedWhileQueued_FailsWithInvalidTarget()
    {
        var harness = ServiceHarness.Create();

        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));
        harness.Targets.ClosePage(ServiceHarness.AgentPage);
        harness.Dispatcher.Pump();

        var response = await pending;

        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task PageVersionAdvancedWhileQueued_FailsWithPageVersionMismatch()
    {
        var harness = ServiceHarness.Create();

        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript,
            harness.JavascriptRequest(ServiceHarness.AgentPage, DesktopPageVersion.Require(1)));

        // 排队期间页面发生了交互（版本推进）：旧的 Snapshot/Locator 作废。
        harness.Targets.UpdatePage(ServiceHarness.AgentPage, DesktopPageVersion.Require(2));
        harness.Dispatcher.Pump();

        var response = await pending;

        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, response.Error!.Code);
        Assert.Equal(0, harness.Surface.JavascriptCount);
    }

    [Fact]
    public async Task WindowClosedWhileQueued_CompletesWithUiUnavailable()
    {
        var harness = ServiceHarness.Create();

        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));
        Assert.False(pending.IsCompleted);

        harness.Service.Close();
        harness.Dispatcher.Pump();

        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, response.Error!.Code);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task DeadlineExpiredWhileQueued_FailsWithoutCallingTheSurface()
    {
        var harness = ServiceHarness.Create();

        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            harness.Context(deadline: TimeSpan.FromMilliseconds(120)));

        await Task.Delay(250);
        harness.Dispatcher.Pump();

        var response = await pending;

        Assert.Equal(DesktopCapabilityErrorCode.DeadlineExceeded, response.Error!.Code);
        Assert.False(response.Error.MayHaveSideEffects);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task RefusedQueue_ReturnsUiUnavailableInsteadOfHanging()
    {
        var harness = ServiceHarness.Create(configureDispatcher: dispatcher => dispatcher.RefuseQueue = true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, response.Error!.Code);
        Assert.True(response.Error.Retryable);
    }

    [Fact]
    public async Task DisposedDispatcher_ReturnsUiUnavailable()
    {
        var harness = ServiceHarness.Create(configureDispatcher: dispatcher => dispatcher.IsDisposed = true);

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, response.Error!.Code);
    }

    [Fact]
    public async Task ClosedService_RejectsNewCallsWithUiUnavailable()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        var closedEvents = 0;
        harness.Service.Closed += () => Interlocked.Increment(ref closedEvents);

        harness.Service.Close();
        harness.Service.Close();

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate, harness.NavigateRequest(ServiceHarness.AgentPage));

        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, response.Error!.Code);
        Assert.True(harness.Service.IsClosed);
        Assert.Equal(1, closedEvents);
    }

    [Fact]
    public async Task CanceledCaller_ReturnsCancelledAndNeverTouchesTheSurface()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            harness.Context(),
            cancellation.Token);

        Assert.Equal(DesktopCapabilityErrorCode.Cancelled, response.Error!.Code);
        // 从未执行 ⇒ 不得声称可能有副作用。
        Assert.False(response.Error.MayHaveSideEffects);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task DeadlineDuringSurfaceCall_ReturnsDeadlineExceededWithSideEffects()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Surface.Gate = gate;

        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            harness.Context(deadline: TimeSpan.FromMilliseconds(150)));

        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopCapabilityErrorCode.DeadlineExceeded, response.Error!.Code);
        Assert.True(response.Error.MayHaveSideEffects);
        Assert.Equal(1, harness.Surface.NavigateCount);
    }

    [Fact]
    public async Task SurfaceHonoringCancellation_MapsToCancelled()
    {
        var harness = ServiceHarness.Create(hasThreadAccess: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Surface.Gate = gate;

        using var cancellation = new CancellationTokenSource();
        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewExecuteJavascript,
            harness.JavascriptRequest(ServiceHarness.AgentPage),
            harness.Context(),
            cancellation.Token);

        await TestWait.UntilAsync(() => harness.Surface.JavascriptCount == 1, "surface call started");
        cancellation.Cancel();

        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopCapabilityErrorCode.Cancelled, response.Error!.Code);
        Assert.True(response.Error.MayHaveSideEffects);
    }
}
