using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatformTests.Services.Orchestration;

[TestClass]
public sealed class AgentOrchestrationCommittedEventSignalTests
{
    [TestMethod]
    public async Task WaitForChangeAsync_DoesNotLoseSignalPublishedBeforeSubscription()
    {
        var signal = new AgentOrchestrationCommittedEventSignal();
        signal.Signal("run-001", 4);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await signal.WaitForChangeAsync("run-001", knownHead: 3, timeout.Token);

        Assert.IsFalse(timeout.IsCancellationRequested);
    }

    [TestMethod]
    public async Task WaitForChangeAsync_WakesWhenCommittedHeadAdvances()
    {
        var signal = new AgentOrchestrationCommittedEventSignal();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var wait = signal.WaitForChangeAsync("run-001", knownHead: 4, timeout.Token);

        signal.Signal("run-001", 5);
        await wait;

        Assert.IsFalse(timeout.IsCancellationRequested);
    }

    [TestMethod]
    public async Task WaitForChangeAsync_BroadcastsOneCommitToAllCurrentWaiters()
    {
        var signal = new AgentOrchestrationCommittedEventSignal();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var first = signal.WaitForChangeAsync("run-001", knownHead: 4, timeout.Token);
        var second = signal.WaitForChangeAsync("run-001", knownHead: 4, timeout.Token);

        signal.Signal("run-001", 5);
        await Task.WhenAll(first.AsTask(), second.AsTask());

        Assert.IsFalse(timeout.IsCancellationRequested);
    }
}
