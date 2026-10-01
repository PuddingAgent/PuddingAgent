using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

public sealed class CommandDispatchTests
{
    [Fact]
    public async Task PageStateCommand_ReturnsPageStateOutcome()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.PageStateCommand("op-state"));
        var result = await harness.Stream.WaitForResultAsync("op-state");

        Assert.Equal(Proto.OperationResult.OutcomeOneofCase.PageState, result.OutcomeCase);
        Assert.Equal("https://example.com/state", result.PageState.Url);
        Assert.Equal(3, result.PageState.PageVersion);
        Assert.Equal("complete", result.PageState.Readiness);

        var call = Assert.Single(harness.Executor.Calls);
        Assert.Equal("ctx-1/page-1", call.Request.Target!.Key);

        var audit = await harness.Audit.WaitForAsync(new OperationId("op-state"));
        Assert.Equal(DesktopCapabilityOutcome.Succeeded, audit.Outcome);
        Assert.Equal("webview.page_state", audit.Capability);
    }

    [Fact]
    public async Task PageStateCommand_WithoutTarget_IsRejectedAsInvalidTarget()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.PageStateCommand("op-state", includeTarget: false));
        var result = await harness.Stream.WaitForResultAsync("op-state");

        Assert.Equal("invalid_target", result.Error.Code);
        Assert.Equal(0, harness.Executor.CallCount);
    }

    [Fact]
    public async Task NavigateCommand_ProducesTypedResultAndAudit()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.NavigateCommand("op-1", expectedPageVersion: 4));
        var result = await harness.Stream.WaitForResultAsync("op-1");

        Assert.Equal(Proto.OperationResult.OutcomeOneofCase.Navigate, result.OutcomeCase);
        Assert.Equal(Proto.NavigateDisposition.Completed, result.Navigate.Disposition);
        Assert.Equal("https://example.com/done", result.Navigate.CurrentUrl);
        Assert.Equal(3, result.Navigate.PageVersion);
        Assert.Equal(1UL, result.Generation);

        var call = Assert.Single(harness.Executor.Calls);
        Assert.Equal("https://example.com/", call.Request.Navigate!.Url.AbsoluteUri);
        Assert.Equal(4, call.Request.Navigate.ExpectedPageVersion.Value);
        Assert.Equal("ctx-1", call.Request.Target!.ContextId);

        var audit = await harness.Audit.WaitForAsync(new OperationId("op-1"));
        Assert.Equal(DesktopCapabilityOutcome.Succeeded, audit.Outcome);
        Assert.Equal("webview.navigate", audit.Capability);
        Assert.Equal(1, audit.Generation.Value);
        Assert.Null(audit.ErrorCode);
    }

    [Fact]
    public async Task JavaScriptCommand_AppliesDefaultResultBudget()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.JavascriptCommand("op-js"));
        var result = await harness.Stream.WaitForResultAsync("op-js");

        Assert.Equal(Proto.OperationResult.OutcomeOneofCase.ExecuteJavascript, result.OutcomeCase);
        Assert.Equal(Proto.JavascriptValueKind.String, result.ExecuteJavascript.Kind);
        Assert.Equal("\"ok\"", result.ExecuteJavascript.JsonValue);

        var call = Assert.Single(harness.Executor.Calls);
        Assert.Equal(JavascriptRequest.DefaultMaxResultBytes, call.Request.Javascript!.MaxResultBytes);
    }

    [Fact]
    public async Task NotificationCommand_MapsPriorityAndReturnsOutcome()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.NotificationCommand("op-n"));
        var result = await harness.Stream.WaitForResultAsync("op-n");

        Assert.Equal(Proto.OperationResult.OutcomeOneofCase.ShowNotification, result.OutcomeCase);
        Assert.True(result.ShowNotification.Shown);

        var call = Assert.Single(harness.Executor.Calls);
        Assert.Equal(DesktopNotificationPriority.High, call.Request.Notification!.Priority);
        Assert.Equal("标题", call.Request.Notification.Title);
    }

    [Fact]
    public async Task PayloadThatDoesNotMatchCapability_IsRejectedWithoutExecution()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.CommandFrame(new Proto.CapabilityCommand
        {
            OperationId = "op-1",
            Generation = 1,
            Capability = "webview.navigate",
            Deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30)),
            ExecuteJavascript = new Proto.ExecuteJavascriptCommand
            {
                Target = new Proto.CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
                Script = "return 1;",
            },
        }));

        var result = await harness.Stream.WaitForResultAsync("op-1");

        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Equal(0, harness.Executor.CallCount);
    }

    [Fact]
    public async Task UnknownCapability_IsRejectedAndChannelStaysUsable()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.CommandFrame(
            Frames.Command("op-bad", "webview.teleport", deadline: DateTimeOffset.UtcNow.AddSeconds(30))));
        var rejected = await harness.Stream.WaitForResultAsync("op-bad");
        Assert.Equal("unsupported_capability", rejected.Error.Code);

        // 单次业务失败不得关闭整条通道。
        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        var ok = await harness.WaitForResultAsync("op-1");
        Assert.Equal(Proto.OperationResult.OutcomeOneofCase.Navigate, ok.OutcomeCase);
    }

    [Fact]
    public async Task UnregisteredCapability_IsRejectedAsUnsupported()
    {
        var options = new HarnessOptions
        {
            Declared = DesktopCapability.WebViewNavigate | (DesktopCapability)(1 << 20),
            Granted = DesktopCapability.WebViewNavigate | (DesktopCapability)(1 << 20),
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.CommandFrame(Frames.Command(
            "op-dialog",
            "not.registered.capability",
            deadline: DateTimeOffset.UtcNow.AddSeconds(30),
            payload: Proto.CapabilityCommand.PayloadOneofCase.Navigate)));
        var result = await harness.Stream.WaitForResultAsync("op-dialog");

        Assert.Equal("unsupported_capability", result.Error.Code);
        Assert.Equal(0, harness.Executor.CallCount);
    }

    [Fact]
    public async Task MalformedCommands_AreRejectedWithStructuredErrors()
    {
        await using var harness = await ConnectionHarness.StartAsync();
        var future = DateTimeOffset.UtcNow.AddSeconds(30);

        // 缺 payload
        harness.Stream.Send(Frames.CommandFrame(Frames.Command("op-nopayload", "webview.navigate", deadline: future)));
        Assert.Equal("invalid_request", (await harness.Stream.WaitForResultAsync("op-nopayload")).Error.Code);

        // 缺 deadline
        harness.Stream.Send(Frames.CommandFrame(Frames.Command(
            "op-nodeadline", "webview.navigate", payload: Proto.CapabilityCommand.PayloadOneofCase.Navigate)));
        Assert.Equal("invalid_request", (await harness.Stream.WaitForResultAsync("op-nodeadline")).Error.Code);

        // 缺目标
        harness.Stream.Send(Frames.NavigateCommand("op-notarget", includeTarget: false));
        Assert.Equal("invalid_target", (await harness.WaitForResultAsync("op-notarget")).Error.Code);

        // 相对 URL
        harness.Stream.Send(Frames.NavigateCommand("op-relative", url: "relative/path"));
        Assert.Equal("invalid_request", (await harness.Stream.WaitForResultAsync("op-relative")).Error.Code);

        // 世代为 0
        harness.Stream.Send(Frames.NavigateCommand("op-zerogen", generation: 0));
        Assert.Equal("invalid_request", (await harness.Stream.WaitForResultAsync("op-zerogen")).Error.Code);

        // 无效 operation id：无法关联结果帧 ⇒ 以 channel_status 事件如实上报并计数
        harness.Stream.Send(Frames.CommandFrame(Frames.Command("bad id", "webview.navigate", deadline: future)));
        await TestWait.UntilAsync(() => harness.Connection.RejectedFrameCount > 0, "rejected frame counted");
        var status = await harness.Stream.WaitForWriteAsync(
            frame => frame.Event?.ChannelStatus?.State == "rejected_command", "rejected_command status event");
        Assert.NotNull(status);

        Assert.Equal(0, harness.Executor.CallCount);
    }

    [Fact]
    public async Task StaleGenerationCommand_IsRejectedAsNotConnected()
    {
        var options = new HarnessOptions { Generation = 5 };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-old", generation: 4));
        var result = await harness.Stream.WaitForResultAsync("op-old");

        Assert.Equal("not_connected", result.Error.Code);
        Assert.Equal(5UL, result.Generation);
        Assert.Equal(0, harness.Executor.CallCount);

        var audit = await harness.Audit.WaitForAsync(new OperationId("op-old"));
        Assert.Equal(DesktopCapabilityOutcome.Rejected, audit.Outcome);
    }

    [Fact]
    public async Task CommandUndeclaredInNegotiation_IsRejectedWithoutExecution()
    {
        var options = new HarnessOptions
        {
            Declared = DesktopCapability.WebViewNavigate | DesktopCapability.ShellNotification,
            Granted = DesktopCapability.WebViewNavigate,
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NotificationCommand("op-n"));
        var result = await harness.Stream.WaitForResultAsync("op-n");

        Assert.Equal("unsupported_capability", result.Error.Code);
        Assert.Equal(0, harness.Executor.CallCount);
    }

    [Fact]
    public async Task ExpiredDeadline_IsRejectedWithoutExecution()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.NavigateCommand("op-late", deadline: DateTimeOffset.UtcNow.AddSeconds(-1)));
        var result = await harness.Stream.WaitForResultAsync("op-late");

        Assert.Equal("deadline_exceeded", result.Error.Code);
        Assert.False(result.Error.MayHaveSideEffects);
        Assert.Equal(0, harness.Executor.CallCount);

        var audit = await harness.Audit.WaitForAsync(new OperationId("op-late"));
        Assert.Equal(DesktopCapabilityOutcome.Rejected, audit.Outcome);
        Assert.Equal(DesktopCapabilityErrorCode.DeadlineExceeded, audit.ErrorCode);
    }

    [Fact]
    public async Task ExecutorIgnoringDeadline_ReturnsDeadlineExceededWithSideEffects()
    {
        var options = new HarnessOptions { ExecutorHandler = Handlers.NeverCompleting };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-slow", deadline: DateTimeOffset.UtcNow.AddMilliseconds(150)));
        var result = await harness.Stream.WaitForResultAsync("op-slow", TimeSpan.FromSeconds(5));

        Assert.Equal("deadline_exceeded", result.Error.Code);
        Assert.True(result.Error.MayHaveSideEffects);

        var audit = await harness.Audit.WaitForAsync(new OperationId("op-slow"));
        Assert.Equal(DesktopCapabilityOutcome.DeadlineExceeded, audit.Outcome);

        // 不再为同一操作写第二条结果。
        await Task.Delay(150);
        Assert.Equal(1, harness.ResultCount("op-slow"));
    }

    [Fact]
    public async Task ExecutorException_MapsToInternalErrorAndKeepsChannelAlive()
    {
        var calls = 0;
        var options = new HarnessOptions
        {
            ExecutorHandler = (call, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return Task.FromException<DesktopCapabilityResponse>(new InvalidOperationException("boom"));
                }

                return Task.FromResult(FakeExecutor.DefaultResponse(call.Capability));
            },
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-throw"));
        var failed = await harness.Stream.WaitForResultAsync("op-throw");
        Assert.Equal("internal_error", failed.Error.Code);
        Assert.DoesNotContain("boom", failed.Error.Message, StringComparison.Ordinal);

        harness.Stream.Send(Frames.NavigateCommand("op-ok"));
        var ok = await harness.Stream.WaitForResultAsync("op-ok");
        Assert.Equal(Proto.OperationResult.OutcomeOneofCase.Navigate, ok.OutcomeCase);
    }

    [Fact]
    public async Task ExecutorReturningMismatchedPayload_MapsToInternalError()
    {
        var options = new HarnessOptions
        {
            ExecutorHandler = (_, _) => Task.FromResult(
                DesktopCapabilityResponse.FromNotification(new DesktopNotificationResult(true, "n-1"))),
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-mismatch"));
        var result = await harness.Stream.WaitForResultAsync("op-mismatch");

        Assert.Equal("internal_error", result.Error.Code);
    }

    [Fact]
    public async Task DuplicateOperationId_WhileRunning_ExecutesOnce()
    {
        var gate = new TaskCompletionSource<DesktopCapabilityResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new HarnessOptions { ExecutorHandler = (_, _) => gate.Task };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await TestWait.UntilAsync(() => harness.Executor.CallCount == 1, "executor started");

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await Task.Delay(100);

        Assert.Equal(1, harness.Executor.CallCount);
        Assert.Equal(1, harness.Connection.PendingOperationCount);

        gate.SetResult(FakeExecutor.DefaultResponse(FakeExecutor.Descriptor(DesktopCapability.WebViewNavigate)));
        var result = await harness.Stream.WaitForResultAsync("op-1");
        Assert.Equal(Proto.OperationResult.OutcomeOneofCase.Navigate, result.OutcomeCase);
        Assert.Equal(1, harness.ResultCount("op-1"));
        Assert.Equal(1, harness.Executor.CallCount);
    }

    [Fact]
    public async Task DuplicateOperationId_WithDifferentPayload_IsRejected()
    {
        var gate = new TaskCompletionSource<DesktopCapabilityResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new HarnessOptions { ExecutorHandler = (_, _) => gate.Task };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await TestWait.UntilAsync(() => harness.Executor.CallCount == 1, "executor started");

        harness.Stream.Send(Frames.NavigateCommand("op-1", url: "https://example.com/other"));
        var rejection = await harness.Stream.WaitForErrorAsync("op-1", "invalid_request");
        Assert.NotNull(rejection);

        gate.SetResult(FakeExecutor.DefaultResponse(FakeExecutor.Descriptor(DesktopCapability.WebViewNavigate)));
        await TestWait.UntilAsync(() => harness.ResultCount("op-1") == 2, "both results written");
        Assert.Equal(1, harness.Executor.CallCount);
    }

    [Fact]
    public async Task DuplicateOperationId_AfterTerminal_ReplaysCachedResult()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        var first = await harness.Stream.WaitForResultAsync("op-1");

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await TestWait.UntilAsync(() => harness.ResultCount("op-1") == 2, "cached result replayed");

        var replayed = harness.FindResult("op-1");
        Assert.Equal(first.Navigate.Disposition, replayed!.Navigate.Disposition);
        Assert.Equal(1, harness.Executor.CallCount);
    }

    [Fact]
    public async Task DuplicateOperationId_AfterCacheExpiry_ReturnsOutcomeUnknownWithoutReexecution()
    {
        var options = new HarnessOptions { TerminalResultTtl = TimeSpan.FromMinutes(1) };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await harness.Stream.WaitForResultAsync("op-1");

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        var result = await harness.Stream.WaitForErrorAsync("op-1", "outcome_unknown");

        Assert.Equal(1, harness.Executor.CallCount);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task InFlightLimit_MakesSecondCommandWaitUntilSlotFrees()
    {
        var gate = new TaskCompletionSource<DesktopCapabilityResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new HarnessOptions { MaxInFlightOperations = 1, ExecutorHandler = (_, _) => gate.Task };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await TestWait.UntilAsync(() => harness.Executor.CallCount == 1, "first command dispatched");

        harness.Stream.Send(Frames.NavigateCommand("op-2"));
        await Task.Delay(120);
        Assert.Equal(1, harness.Executor.CallCount);
        Assert.Equal(2, harness.Connection.PendingOperationCount);

        gate.SetResult(FakeExecutor.DefaultResponse(FakeExecutor.Descriptor(DesktopCapability.WebViewNavigate)));
        await harness.Stream.WaitForResultAsync("op-1");
        await harness.Stream.WaitForResultAsync("op-2");
        Assert.Equal(2, harness.Executor.CallCount);
    }

    [Fact]
    public async Task InFlightLimit_WhenWaitExceedsDeadline_ReturnsResourceExhausted()
    {
        var options = new HarnessOptions
        {
            MaxInFlightOperations = 1,
            ExecutorHandler = Handlers.NeverCompleting,
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await TestWait.UntilAsync(() => harness.Executor.CallCount == 1, "first command dispatched");

        harness.Stream.Send(Frames.NavigateCommand("op-2", deadline: DateTimeOffset.UtcNow.AddMilliseconds(200)));
        var result = await harness.Stream.WaitForResultAsync("op-2", TimeSpan.FromSeconds(5));

        Assert.Equal("resource_exhausted", result.Error.Code);
        Assert.True(result.Error.Retryable);
        Assert.False(result.Error.MayHaveSideEffects);
        Assert.Equal(1, harness.Executor.CallCount);
    }

    [Fact]
    public async Task MutatingCommandsOnTheSameTarget_AreSerialized()
    {
        var running = 0;
        var maxConcurrent = 0;
        var options = new HarnessOptions
        {
            ExecutorHandler = async (call, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref running);
                maxConcurrent = Math.Max(maxConcurrent, current);
                await Task.Delay(80, cancellationToken);
                Interlocked.Decrement(ref running);
                return FakeExecutor.DefaultResponse(call.Capability);
            },
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        for (var index = 0; index < 4; index++)
        {
            harness.Stream.Send(Frames.NavigateCommand($"op-same-{index}"));
        }

        for (var index = 0; index < 4; index++)
        {
            await harness.Stream.WaitForResultAsync($"op-same-{index}");
        }

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(4, harness.Executor.CallCount);
    }

    [Fact]
    public async Task MutatingCommandsOnDifferentTargets_RunConcurrently()
    {
        var running = 0;
        var maxConcurrent = 0;
        var options = new HarnessOptions
        {
            ExecutorHandler = async (call, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref running);
                maxConcurrent = Math.Max(maxConcurrent, current);
                await Task.Delay(80, cancellationToken);
                Interlocked.Decrement(ref running);
                return FakeExecutor.DefaultResponse(call.Capability);
            },
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        for (var index = 0; index < 3; index++)
        {
            harness.Stream.Send(Frames.NavigateCommand($"op-{index}", contextId: $"ctx-{index}", pageId: $"page-{index}"));
        }

        for (var index = 0; index < 3; index++)
        {
            await harness.Stream.WaitForResultAsync($"op-{index}");
        }

        Assert.True(maxConcurrent >= 2, $"expected concurrent execution on distinct targets, saw {maxConcurrent}");
    }

    [Fact]
    public async Task CancelWhileRunning_ReturnsCancelledWithSideEffectFlag()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new HarnessOptions
        {
            ExecutorHandler = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return DesktopCapabilityResponse.Failure(DesktopCapabilityError.Internal("unreachable"));
            },
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Stream.Send(Frames.CancelCommand("op-1"));
        var result = await harness.Stream.WaitForResultAsync("op-1");

        Assert.Equal("cancelled", result.Error.Code);
        Assert.True(result.Error.MayHaveSideEffects);

        var audit = await harness.Audit.WaitForAsync(new OperationId("op-1"));
        Assert.Equal(DesktopCapabilityOutcome.Cancelled, audit.Outcome);
    }

    [Fact]
    public async Task CancelFromStaleGeneration_IsIgnored()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<DesktopCapabilityResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new HarnessOptions
        {
            Generation = 3,
            ExecutorHandler = (_, _) =>
            {
                started.TrySetResult();
                return gate.Task;
            },
        };

        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-1", generation: 3));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Stream.Send(Frames.CancelCommand("op-1", generation: 2));
        await Task.Delay(120);
        Assert.Null(harness.FindResult("op-1"));

        harness.Stream.Send(Frames.CancelCommand("op-1", generation: 3));
        var result = await harness.Stream.WaitForResultAsync("op-1");
        Assert.Equal("cancelled", result.Error.Code);
    }

    [Fact]
    public async Task InvalidCorrelationId_IsIgnoredRatherThanFailingTheCommand()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.NavigateCommand("op-1", correlationId: "not a valid id"));
        await harness.Stream.WaitForResultAsync("op-1");

        var call = Assert.Single(harness.Executor.Calls);
        Assert.Null(call.Context.CorrelationId);
    }

    [Fact]
    public async Task ValidCorrelationId_FlowsIntoContextAndAudit()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.NavigateCommand("op-1", traceId: "trace-7", correlationId: "call-7"));
        await harness.Stream.WaitForResultAsync("op-1");

        var audit = await harness.Audit.WaitForAsync(new OperationId("op-1"));
        Assert.Equal("trace-7", audit.TraceId);
        Assert.Equal("call-7", audit.CorrelationId!.Value);
        Assert.True(audit.QueueDuration >= TimeSpan.Zero);
    }
}
