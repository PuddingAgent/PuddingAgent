using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerTests;

/// <summary>命令下发、结果关联、期限/取消/在途额度与幂等语义。</summary>
public sealed class InvokeAndCorrelationTests
{
    [Fact]
    public async Task Navigate_SendsCommandAndCompletesFromResult()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var call = harness.Call("op-nav");

        var pending = harness.Session.NavigateAsync(
            new NavigateRequest(BrokerHarness.Target, new Uri("https://example.com/a"), DesktopPageVersion.Require(2)), call);

        var command = await harness.WaitForCommandAsync("op-nav");
        Assert.NotNull(command);
        Assert.Equal("webview.navigate", command!.Capability);
        Assert.Equal(1UL, command.Generation);
        Assert.Equal("ctx-1", command.Navigate.Target.ContextId);
        Assert.Equal("https://example.com/a", command.Navigate.Url);
        Assert.Equal(2, command.Navigate.ExpectedPageVersion);
        Assert.Equal(call.DeadlineUtc, command.Deadline.ToDateTimeOffset());

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-nav", 1)));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal(Pudding.Contracts.Desktop.NavigateDisposition.Completed, result.Value.Disposition);
        Assert.Equal("https://example.com/done", result.Value.CurrentUrl!.AbsoluteUri);
        Assert.Equal(0, harness.Session.PendingOperationCount);

        var audit = Assert.Single(harness.Audit.ForOperation(new OperationId("op-nav")));
        Assert.Equal(DesktopCapabilityOutcome.Succeeded, audit.Outcome);
        Assert.Equal("webview.navigate", audit.Capability);
    }

    [Fact]
    public async Task EveryCapability_RoundTripsItsTypedResult()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var javascript = harness.Session.ExecuteJavascriptAsync(BrokerHarness.Javascript(), harness.Call("op-js"));
        await harness.WaitForCommandAsync("op-js");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.JavascriptOk("op-js", 1, "{\"a\":1}")));
        var javascriptResult = await javascript.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JavascriptValueKind.String, javascriptResult.Value.Kind);
        Assert.Equal("{\"a\":1}", javascriptResult.Value.JsonValue);

        var state = harness.Session.GetPageStateAsync(BrokerHarness.Target, harness.Call("op-state"));
        await harness.WaitForCommandAsync("op-state");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.PageStateOk("op-state", 1, readiness: "interactive")));
        var stateResult = await state.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopPageReadiness.Interactive, stateResult.Value.Readiness);
        Assert.Equal(5, stateResult.Value.Version.Value);
        // 结果帧不重复携带目标：领域 DTO 的目标来自请求关联。
        Assert.Equal(BrokerHarness.Target, stateResult.Value.Target);

        var notification = harness.Session.ShowNotificationAsync(BrokerHarness.Notification(), harness.Call("op-n"));
        await harness.WaitForCommandAsync("op-n");
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NotificationOk("op-n", 1)));
        var notificationResult = await notification.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(notificationResult.Value.Shown);
        Assert.Equal("n-1", notificationResult.Value.NotificationId);

        // 无参数能力：请求不携带目标，结果由 DesktopService 补齐自动化状态与页面数。
        var status = harness.Session.GetShellStatusAsync(harness.Call("op-status"));
        var statusCommand = await harness.WaitForCommandAsync("op-status");
        Assert.Equal("shell.status", statusCommand!.Capability);
        Assert.NotNull(statusCommand.GetShellStatus);
        Assert.Null(statusCommand.Navigate);
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.ShellStatusOk("op-status", 1)));
        var statusResult = await status.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopWindowState.HiddenToTray, statusResult.Value.WindowState);
        Assert.True(statusResult.Value.TrayVisible);
        Assert.Equal(DesktopAutomationState.Free, statusResult.Value.Automation);
        Assert.Equal(3, statusResult.Value.OpenPageCount);
    }

    [Fact]
    public async Task Invoke_AfterDisconnect_IsNotConnected()
    {
        await using var harness = await BrokerHarness.StartAsync();
        harness.Channel.PushClose();
        await TestWait.UntilAsync(
            () => harness.Session.State == DesktopLinkState.Disconnected, "session observes the closed stream");

        var result = await harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-late"));

        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, result.Error!.Code);
    }

    [Fact]
    public async Task Invoke_WhenCallTargetsAnotherDesktop_IsInvalidTarget()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var call = new DesktopCallContext(
            new DesktopInstanceId("desk-other"),
            new OperationId("op-1"),
            DateTimeOffset.UtcNow.AddSeconds(30));

        var result = await harness.Session.NavigateAsync(BrokerHarness.Navigate(), call);

        Assert.Equal(DesktopCapabilityErrorCode.InvalidTarget, result.Error!.Code);
        Assert.Empty(harness.Channel.Commands);
    }

    [Fact]
    public async Task Invoke_WithoutConfiguredAuthorizer_IsDeniedByDefault()
    {
        await using var harness = await BrokerHarness.StartAsync(
            new HarnessOptions { Authorizer = null });

        var result = await harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));

        // RPC 可达 ≠ 获得桌面操作授权：默认 fail closed。
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);
        Assert.Empty(harness.Channel.Commands);
    }

    [Fact]
    public async Task Invoke_WhenAuthorizerDenies_IsUnauthorizedAndAudited()
    {
        var denial = DesktopCapabilityError.Unauthorized("policy says no");
        await using var harness = await BrokerHarness.StartAsync(
            new HarnessOptions { Authorizer = new FixedAuthorizer(denial) });

        var result = await harness.Session.ExecuteJavascriptAsync(BrokerHarness.Javascript(), harness.Call("op-1"));

        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);
        Assert.Empty(harness.Channel.Commands);
        Assert.Equal(
            DesktopCapabilityOutcome.Rejected,
            Assert.Single(harness.Audit.ForOperation(new OperationId("op-1"))).Outcome);
    }

    [Fact]
    public async Task Invoke_WithExpiredDeadline_IsRejectedWithoutSendingOrSideEffects()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var result = await harness.Session.NavigateAsync(
            BrokerHarness.Navigate(), harness.Call("op-1", TimeSpan.FromSeconds(-1)));

        Assert.Equal(DesktopCapabilityErrorCode.DeadlineExceeded, result.Error!.Code);
        Assert.False(result.Error.MayHaveSideEffects);
        Assert.Empty(harness.Channel.Commands);
    }

    [Fact]
    public async Task Invoke_SurfacesDesktopErrorOutcomeWithItsSemantics()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var pending = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.Error(
            "op-1", 1, "page_version_mismatch", "stale locator", retryable: true, mayHaveSideEffects: false)));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, result.Error!.Code);
        Assert.Equal("stale locator", result.Error.Message);
        Assert.True(result.Error.Retryable);
        Assert.False(result.Error.MayHaveSideEffects);

        var audit = Assert.Single(harness.Audit.ForOperation(new OperationId("op-1")));
        Assert.Equal(DesktopCapabilityOutcome.Failed, audit.Outcome);
        Assert.Equal(DesktopCapabilityErrorCode.PageVersionMismatch, audit.ErrorCode);
    }

    [Fact]
    public async Task Invoke_WhenResultPayloadDoesNotMatchTheCommand_IsInternalError()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var pending = harness.Session.ExecuteJavascriptAsync(BrokerHarness.Javascript(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-1", 1)));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, result.Error!.Code);
    }

    [Fact]
    public async Task Invoke_IgnoresUnknownOperationResults()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var pending = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-unknown", 1)));
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-1", 1)));
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task Invoke_IgnoresResultsFromAStaleGeneration()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var pending = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");

        // 旧世代的结果绝不能完成新世代的命令。
        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-1", 99)));
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-1", 1)));
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task Invoke_DuplicateOperationIdWithSamePayload_ReusesTheInFlightCall()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var call = harness.Call("op-dup");

        var first = harness.Session.NavigateAsync(BrokerHarness.Navigate(), call);
        await harness.WaitForCommandAsync("op-dup");
        var second = harness.Session.NavigateAsync(BrokerHarness.Navigate(), call);

        await Task.Delay(100);
        Assert.Single(harness.Channel.Commands, command => command.OperationId == "op-dup");

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-dup", 1)));

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstResult.IsSuccess);
        Assert.True(secondResult.IsSuccess);
        Assert.Equal(firstResult.Value.CurrentUrl, secondResult.Value.CurrentUrl);
    }

    [Fact]
    public async Task Invoke_DuplicateOperationIdWithDifferentPayload_IsRejected()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var call = harness.Call("op-dup");

        var first = harness.Session.NavigateAsync(BrokerHarness.Navigate("https://example.com/a"), call);
        await harness.WaitForCommandAsync("op-dup");

        var second = await harness.Session.NavigateAsync(BrokerHarness.Navigate("https://example.com/b"), call);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, second.Error!.Code);

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-dup", 1)));
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task Invoke_WhenInFlightLimitIsReached_ReturnsResourceExhausted()
    {
        var options = new HarnessOptions { MaxInFlightPerConnection = 1 };
        await using var harness = await BrokerHarness.StartAsync(options);

        var first = harness.Session.NavigateAsync(BrokerHarness.Navigate(), harness.Call("op-1"));
        await harness.WaitForCommandAsync("op-1");

        var second = await harness.Session.NavigateAsync(
            BrokerHarness.Navigate(), harness.Call("op-2", TimeSpan.FromMilliseconds(200)));

        Assert.Equal(DesktopCapabilityErrorCode.ResourceExhausted, second.Error!.Code);
        Assert.True(second.Error.Retryable);

        harness.Channel.Push(DesktopFrames.Result(DesktopFrames.NavigateOk("op-1", 1)));
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task Invoke_WhenCallerCancels_ReturnsCancelledAndSendsCancelFrame()
    {
        await using var harness = await BrokerHarness.StartAsync();
        using var cancellation = new CancellationTokenSource();

        var pending = harness.Session.NavigateAsync(
            BrokerHarness.Navigate(), harness.Call("op-1"), cancellation.Token);
        await harness.WaitForCommandAsync("op-1");

        cancellation.Cancel();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DesktopCapabilityErrorCode.Cancelled, result.Error!.Code);
        Assert.True(result.Error.MayHaveSideEffects);

        var cancelFrame = await harness.WaitForCancelAsync("op-1");
        Assert.Equal(1UL, cancelFrame!.Cancel.Generation);
    }

    [Fact]
    public async Task Invoke_DeadlineWhilePending_ReturnsDeadlineExceededWithSideEffects()
    {
        await using var harness = await BrokerHarness.StartAsync();

        var pending = harness.Session.NavigateAsync(
            BrokerHarness.Navigate(), harness.Call("op-1", TimeSpan.FromMilliseconds(150)));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopCapabilityErrorCode.DeadlineExceeded, result.Error!.Code);
        // 命令已经发出：可能已产生副作用。
        Assert.True(result.Error.MayHaveSideEffects);
        Assert.Equal(
            DesktopCapabilityOutcome.DeadlineExceeded,
            Assert.Single(harness.Audit.ForOperation(new OperationId("op-1"))).Outcome);
    }

    [Fact]
    public async Task Cancel_UnknownOperation_ReturnsFalse()
    {
        await using var harness = await BrokerHarness.StartAsync();

        Assert.False(await harness.Session.CancelAsync(new OperationId("op-missing")));
    }

    [Fact]
    public async Task EventsFromDesktop_AreForwarded()
    {
        await using var harness = await BrokerHarness.StartAsync();
        var received = new TaskCompletionSource<Proto.DesktopEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.EventReceived += @event => received.TrySetResult(@event);

        harness.Channel.Push(DesktopFrames.Event("complete"));

        var @event = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("complete", @event.PageState.State);
    }

    private sealed class FixedAuthorizer : IDesktopCapabilityAuthorizer
    {
        private readonly DesktopCapabilityError _error;

        public FixedAuthorizer(DesktopCapabilityError error) => _error = error;

        public ValueTask<DesktopCapabilityError?> AuthorizeAsync(
            DesktopCapabilityAuthorizationContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DesktopCapabilityError?>(_error);
    }
}
