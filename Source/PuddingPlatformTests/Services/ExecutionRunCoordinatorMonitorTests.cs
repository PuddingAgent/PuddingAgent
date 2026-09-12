using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ExecutionRunCoordinatorMonitorTests
{
    [TestMethod]
    public async Task InboxFailure_CancelsRunAndCannotBecomeSuccessfulTerminal()
    {
        var coordinator = Create(new FailingInbox());
        using var run = new CancellationTokenSource();
        using var monitor = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outcome = await coordinator.MonitorAsync(Lease(), run, null, TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(1), monitor.Token);
        Assert.IsTrue(run.IsCancellationRequested);
        Assert.IsTrue(outcome.Failed);
        var terminal = ExecutionRunCoordinator.ApplyMonitorOutcome(TurnTerminal.Success(null, null), outcome, null, TimeSpan.FromMinutes(30));
        Assert.AreEqual("execution_monitor_failed", terminal.ErrorCode);
    }

    [TestMethod]
    public async Task NormalMonitorShutdown_DoesNotCancelRunOrReportFault()
    {
        var coordinator = Create(new FailingInbox());
        using var run = new CancellationTokenSource();
        using var monitor = new CancellationTokenSource();
        monitor.Cancel();
        var outcome = await coordinator.MonitorAsync(Lease(), run, null, TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(1), monitor.Token);
        Assert.IsFalse(run.IsCancellationRequested);
        Assert.IsFalse(outcome.Failed);
    }

    private static ExecutionRunCoordinator Create(IControlInbox inbox) => new(
        null!, null!, null!, null!, null!, null!, null!, inbox, null!, null!,
        NullLogger<ExecutionRunCoordinator>.Instance);

    private static ExecutionLease Lease() => new("command", "worker", "ws", "conversation", "turn", "run", 1, DateTimeOffset.UtcNow.AddMinutes(2)) { TraceId = "trace" };

    private sealed class FailingInbox : IControlInbox
    {
        public Task<IReadOnlyList<ControlMessageRecord>> ReadPendingAsync(ExecutionLease lease, long afterSequence, CancellationToken ct)
            => throw new IOException("injected control store failure");
        public Task AcknowledgeAsync(ExecutionLease lease, string controlId, CancellationToken ct) => Task.CompletedTask;
    }
}
