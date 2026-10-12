using System.Text.Json;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

public sealed class BrowserDeliveryReviewBridge
{
    private sealed class CountingHandler : IBrowserCommandHandler
    {
        public int Count;
        public TaskCompletionSource<bool>? Gate;
        public async Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            return new BrowserBridgeCommandResult { OperationId = command.OperationId, Success = true };
        }
    }

    private static BrowserBridgeCommand Command(Guid? id = null, string caller = "agent-a") => new()
    {
        OperationId = id ?? Guid.NewGuid(), ContextId = "ctx", PageId = "page",
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = caller, TaskId = "run-" + caller },
        Name = BrowserBridgeCommandNames.PageGoto, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" })
    };

    [Fact]
    public async Task BridgeWithNoPageGrantMustRejectBeforeHandler()
    {
        var dispatcher = BrowserTestState.CreateDispatcher();
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);
        var result = await dispatcher.DispatchAsync(Command(), CancellationToken.None);
        Assert.False(result.Success, $"no grant but handler calls={handler.Count}");
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task CachedResultMustNotBeReplayedToAnotherCaller()
    {
        var dispatcher = BrowserTestState.CreateDispatcher();
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);
        var id = Guid.NewGuid();
        var first = await dispatcher.DispatchAsync(Command(id), CancellationToken.None);
        var second = await dispatcher.DispatchAsync(Command(id, "agent-b"), CancellationToken.None);
        Assert.False(second.Success, "result replay ignored the caller identity");
    }

    [Fact]
    public async Task ConcurrentDuplicatesMustNotExecuteTwice()
    {
        var dispatcher = BrowserTestState.CreateDispatcher();
        var handler = new CountingHandler { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        dispatcher.SetHandler(handler);
        var command = Command();
        var first = dispatcher.DispatchAsync(command, CancellationToken.None);
        var second = dispatcher.DispatchAsync(command, CancellationToken.None);
        handler.Gate.SetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handler.Count <= 1, $"duplicate handler executions={handler.Count}");
    }
}
