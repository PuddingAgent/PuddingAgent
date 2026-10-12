using System.Text.Json;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// R6 要求的**正向**用例：**合法授权**的调用必须仍然成功。
///
/// 存在的理由（方案 §8.1 / 复核 README）：反例要求"无页面授权时于 handler 之前拒绝"，
/// 但**不能**用"一律拒绝"来让它变绿 —— 那会把 Bridge 写操作整体打死。因此这里与
/// <c>TestScripts/BrowserDeliveryReview/BridgeReview.cs</c> 的无授权反例**配对**：
/// 反例证明"没授权不许执行"，本用例证明"有授权照样执行"。
/// </summary>
public sealed class BrowserBridgeAuthorizationFixtureTests
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

    private static BrowserBridgeCommand Command(Guid? id = null) => new()
    {
        OperationId = id ?? Guid.NewGuid(),
        ContextId = "ctx",
        PageId = "page",
        Caller = new BrowserBridgeCallerIdentity { Kind = "agent", CallerId = "agent-a", TaskId = "run-agent-a" },
        Name = BrowserBridgeCommandNames.PageGoto,
        DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" }),
    };

    [Fact]
    public async Task AuthorizedCaller_IsNotBlockedByTheAdmissionGate()
    {
        // 反向对照：没有授权时反例要求被拒；**有授权时必须成功**，否则就是"靠一律拒绝取绿"。
        var lifecycle = BrowserTestState.CreateLifecycle();
        BrowserTestState.GrantAgentPage(lifecycle);
        var dispatcher = new BrowserBridgeCommandDispatcher(lifecycle);
        var handler = new CountingHandler();
        dispatcher.SetHandler(handler);

        var result = await dispatcher.DispatchAsync(Command(), CancellationToken.None);

        Assert.True(result.Success, $"authorized command must still execute; error={result.ErrorCode}");
        Assert.Equal(1, handler.Count);
    }
}
