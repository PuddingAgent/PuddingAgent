using System.Text.Json;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// I2-c 接线缝：执行器路由优先、未覆盖命令回退 handler、路由异常不回退（避免双执行）。
/// 本轮只验证派发器语义；Shell 侧路由实现与真实执行器接线在后续切片。
/// </summary>
public sealed class BrowserBridgeExecutorRouteTests
{
    private sealed class CountingHandler : IBrowserCommandHandler
    {
        public int Count;

        public Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult(new BrowserBridgeCommandResult
            {
                OperationId = command.OperationId,
                Success = true,
            });
        }
    }

    private static BrowserBridgeCommand Command(string name = BrowserBridgeCommandNames.PageGetInfo) => new()
    {
        OperationId = Guid.NewGuid(),
        ContextId = "ctx",
        PageId = "page",
        Origin = BrowserTestState.Origin(),
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = "session-agent-a", TaskId = "run-a" },
        Name = name,
        DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        Arguments = JsonSerializer.SerializeToElement(new { }),
    };

    [Fact]
    public async Task ExecutorRoute_TakesTheCommandAndTheHandlerIsNotCalled()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle);
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        BrowserBridgeCommand? routed = null;
        dispatcher.SetExecutorRoute((command, ct) =>
        {
            routed = command;
            return Task.FromResult<BrowserBridgeCommandResult?>(new BrowserBridgeCommandResult
            {
                OperationId = command.OperationId,
                Success = true,
            });
        });

        var command = Command();
        var result = await dispatcher.DispatchAsync(command, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Same(command, routed);
        Assert.True(dispatcher.HasExecutorRoute);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ExecutorRoute_ReturningNullFallsBackToTheHandlerForUncoveredCommands()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle);
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);
        dispatcher.SetExecutorRoute((_, _) => Task.FromResult<BrowserBridgeCommandResult?>(null));

        var result = await dispatcher.DispatchAsync(Command(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task ExecutorRoute_ExceptionDoesNotSilentlyFallBackToTheHandler()
    {
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.HandOver(lifecycle);
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);
        dispatcher.SetExecutorRoute((_, _) => throw new InvalidOperationException("executor exploded"));

        var result = await dispatcher.DispatchAsync(Command(), CancellationToken.None);

        // 结果未知：派发器返回失败（不抛、也不回退），绝不能悄悄执行第二遍。
        Assert.False(result.Success);
        Assert.Equal(0, handler.Count);
    }
}
