using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SessionExecutionGateTests
{
    [TestMethod]
    public async Task EnterAsync_SerializesExecutionsForSameSession()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var first = await gate.EnterAsync("session-1", "first");

        var secondTask = gate.EnterAsync("session-1", "second").AsTask();
        await Task.Delay(50);
        Assert.IsFalse(secondTask.IsCompleted);

        await first.DisposeAsync();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task EnterAsync_AllowsDifferentSessionsToRunConcurrently()
    {
        var gate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        await using var first = await gate.EnterAsync("session-1", "first");

        var secondTask = gate.EnterAsync("session-2", "second").AsTask();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(secondTask.IsCompletedSuccessfully);
    }
}
