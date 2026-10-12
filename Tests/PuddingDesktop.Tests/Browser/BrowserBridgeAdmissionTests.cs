using System.Text.Json;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// **Bridge 准入与幂等的不变量套件**（2026-10-12 由外部复核反例迁入常规测试）。
///
/// ① **无页面授权**的命令必须在触碰 handler **之前**被拒（fail closed）；
/// ② 幂等缓存**不得跨调用者回放**（同 operationId 换调用者 ⇒ 冲突）；
/// ③ 同一 operationId 的**并发**调用只允许执行一次（后来者等待同一完成源）。
///
/// 正向配对见 <see cref="BrowserBridgeAuthorizationFixtureTests"/>：**有授权必须仍能成功** ——
/// 防止用"一律拒绝"把反例刷绿。
///
/// 原反例文件仍保留在 <c>TestScripts/BrowserDeliveryReview/BridgeReview.cs</c>（外部复核证据与重演路径）。
/// </summary>
public sealed class BrowserBridgeAdmissionTests
{
    private sealed class CountingHandler : IBrowserCommandHandler
    {
        public int Count;
        public TaskCompletionSource<bool>? Gate;

        public async Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            return new BrowserBridgeCommandResult { OperationId = command.OperationId, Success = true };
        }
    }

    private static BrowserBridgeCommand Command(Guid? id = null, string caller = "agent-a") => new()
    {
        OperationId = id ?? Guid.NewGuid(),
        ContextId = "ctx",
        PageId = "page",
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = caller, TaskId = "run-" + caller },
        Name = BrowserBridgeCommandNames.PageGoto,
        DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" }),
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
