using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.DesktopConnection;

namespace DesktopConnectionTests;

public sealed class BackpressureAndDisconnectTests
{
    [Fact]
    public async Task Heartbeat_IsAcknowledgedWithTheSameSequence()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.Heartbeat(7));
        var ack = await harness.Stream.WaitForWriteAsync(
            frame => frame.HeartbeatAck is not null, "heartbeat ack");

        Assert.Equal(7, ack.HeartbeatAck.Sequence);
    }

    [Fact]
    public async Task BestEffortFrames_AreDroppedAndCountedWhenTheBudgetIsExhausted()
    {
        var options = new HarnessOptions { MaxQueuedBytes = 4096 };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.BlockWrites();
        for (var index = 0; index < 3000; index++)
        {
            harness.Stream.Send(Frames.Heartbeat(index));
        }

        await TestWait.UntilAsync(
            () => harness.Connection.DroppedBestEffortFrameCount > 0,
            "best-effort frames are dropped once the byte budget is full");

        Assert.True(harness.Connection.QueuedBytes > 0);
        harness.Stream.ReleaseWrites();
    }

    [Fact]
    public async Task TerminalResult_IsNotDroppedWhenTheBudgetIsTight()
    {
        var options = new HarnessOptions { MaxQueuedBytes = 4096 };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.BlockWrites();
        for (var index = 0; index < 3000; index++)
        {
            harness.Stream.Send(Frames.Heartbeat(index));
        }

        await TestWait.UntilAsync(
            () => harness.Connection.DroppedBestEffortFrameCount > 0,
            "byte budget exhausted by best-effort frames");

        harness.Stream.Send(Frames.NotificationCommand("op-1"));
        await TestWait.UntilAsync(() => harness.Executor.CallCount == 1, "executor ran");
        await Task.Delay(100);

        // 终态结果既不丢弃也不越队：仍在等待字节预算。
        Assert.Null(harness.FindResult("op-1"));

        harness.Stream.ReleaseWrites();
        var result = await harness.Stream.WaitForResultAsync("op-1", TimeSpan.FromSeconds(15));
        Assert.True(result.ShowNotification.Shown);
    }

    [Fact]
    public async Task Disconnect_CompletesPendingAsOutcomeUnknownOrDisconnected()
    {
        var options = new HarnessOptions { MaxInFlightOperations = 1, ExecutorHandler = Handlers.NeverCompleting };
        await using var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-running"));
        await TestWait.UntilAsync(() => harness.Executor.CallCount == 1, "first operation started");

        harness.Stream.Send(Frames.NavigateCommand("op-waiting"));
        await TestWait.UntilAsync(
            () => harness.Connection.PendingOperationCount == 2, "second operation is queued behind the in-flight limit");

        harness.Stream.CloseFromServer();
        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Disconnected, outcome.FinalState);
        Assert.Null(outcome.Error);
        Assert.Equal(0, harness.Connection.PendingOperationCount);

        var running = await harness.Audit.WaitForAsync(new OperationId("op-running"));
        Assert.Equal(DesktopCapabilityOutcome.Disconnected, running.Outcome);
        Assert.Equal(DesktopCapabilityErrorCode.OutcomeUnknown, running.ErrorCode);

        var waiting = await harness.Audit.WaitForAsync(new OperationId("op-waiting"));
        Assert.Equal(DesktopCapabilityOutcome.Disconnected, waiting.Outcome);
        Assert.Equal(DesktopCapabilityErrorCode.Disconnected, waiting.ErrorCode);

        Assert.Single(harness.Audit.ForOperation(new OperationId("op-running")));
        Assert.Single(harness.Audit.ForOperation(new OperationId("op-waiting")));
    }

    [Fact]
    public async Task ServerClosesStream_EndsCleanlyAndHalfClosesRequestStream()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.CloseFromServer();
        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Disconnected, outcome.FinalState);
        Assert.Null(outcome.Error);
        Assert.True(harness.Stream.RequestStreamCompleted);
    }

    [Fact]
    public async Task EmptyFrame_FaultsTheConnection()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.Empty());
        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, outcome.Error!.Code);
    }

    [Fact]
    public async Task SecondHelloAck_FaultsTheConnection()
    {
        await using var harness = await ConnectionHarness.StartAsync();

        harness.Stream.Send(Frames.HelloAck());
        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, outcome.Error!.Code);
    }

    [Fact]
    public async Task InactivityWatchdog_FaultsWhenCoreGoesSilent()
    {
        var options = new HarnessOptions { InactivityTimeout = TimeSpan.FromMilliseconds(250) };
        await using var harness = await ConnectionHarness.StartAsync(options);

        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(DesktopConnectionState.Faulted, outcome.FinalState);
        Assert.Equal(DesktopCapabilityErrorCode.NotConnected, outcome.Error!.Code);
    }

    [Fact]
    public async Task Dispose_EndsTheRunAndReleasesPendingOperations()
    {
        var options = new HarnessOptions { ExecutorHandler = Handlers.NeverCompleting };
        var harness = await ConnectionHarness.StartAsync(options);

        harness.Stream.Send(Frames.NavigateCommand("op-1"));
        await TestWait.UntilAsync(() => harness.Executor.CallCount == 1, "operation started");

        await harness.DisposeAsync();
        var outcome = await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DesktopConnectionState.Disconnected, outcome.FinalState);
        Assert.Equal(0, harness.Connection.PendingOperationCount);
        var audit = await harness.Audit.WaitForAsync(new OperationId("op-1"));
        Assert.Equal(DesktopCapabilityOutcome.Disconnected, audit.Outcome);
        Assert.Equal(DesktopCapabilityErrorCode.OutcomeUnknown, audit.ErrorCode);
    }
}
