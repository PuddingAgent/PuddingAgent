using System.Text.Json;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// 方案 §4.1 的**最后一句**："deadline、correlationId 等非语义元数据不进 hash，
/// 但**每次接收仍检查当前请求是否过期**"。
/// 实现要点（本轮前移）：过期检查位于**账本裁决与 handler 之前** ⇒ 覆盖**重放**路径 ——
/// 否则同一个 operationId 在期限之后重传会静默拿到历史结果，掩盖"本次请求已过期"。
/// </summary>
public sealed class BrowserBridgeDeadlineTests
{
    private sealed class CountingHandler : IBrowserCommandHandler
    {
        public int Count;

        public Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult(new BrowserBridgeCommandResult { OperationId = command.OperationId, Success = true });
        }
    }

    private static BrowserBridgeCommand Command(Guid id, DateTimeOffset deadline) => new()
    {
        OperationId = id,
        ContextId = "ctx",
        PageId = "page",
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = "agent-a", TaskId = "run-a" },
        Name = BrowserBridgeCommandNames.PageGoto,
        DeadlineUtc = deadline,
        Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" }),
    };

    [Fact]
    public async Task AnAlreadyExpiredCommandIsRejectedBeforeAnythingElseHappens()
    {
        var dispatcher = BrowserTestState.CreateDispatcher();
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        var result = await dispatcher.DispatchAsync(
            Command(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None);

        // 过期 ⇒ 明确拒绝（而不是走到授权/账本/handler 才失败）。
        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserDeadlineExceeded, result.ErrorCode);
        // 且**绝不触碰 handler**。
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task AnExpiredRetransmissionIsNotAnsweredWithAStaleReplay()
    {
        var dispatcher = BrowserTestState.CreateDispatcher();
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        // 同一个 operationId 的两次请求：先未过期（可能被授权层拒绝，但绝不因过期被拒），
        // 再**已过期** ⇒ 后者必须是 deadline_exceeded，而**不是**任何历史结果或授权错误。
        var id = Guid.NewGuid();
        var first = await dispatcher.DispatchAsync(Command(id, DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
        Assert.NotEqual(BrowserBridgeErrorCodes.BrowserDeadlineExceeded, first.ErrorCode);

        var second = await dispatcher.DispatchAsync(Command(id, DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None);
        Assert.False(second.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserDeadlineExceeded, second.ErrorCode);
    }
}