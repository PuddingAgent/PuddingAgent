using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace DesktopServiceTests;

/// <summary>
/// 触碰页面前的复检（§4.2）：**排队期间授权被撤销**时，操作必须在触碰页面前被拒，且**驱动调用数为 0**。
///
/// 编排手法：注入的"当前连接世代"提供者在 **第 2 次被调用**时撤销授权。
/// 第 1 次是准入构造 Admit 请求；第 2 次正是"触碰页面前重建复检请求"那一步 ——
/// 于是撤销恰好发生在准入之后、驱动调用之前（无需 sleep、无竞态）。
/// </summary>
public sealed class BrowserRevalidateBeforeTouchingTests
{
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(33);
    private static readonly BrowserCallerIdentity Agent = new(BrowserCallerKind.Agent, "agent-1", "run-1");
    private static readonly BrowserPageGrantId GrantId = new("grant-revalidate");

    [Fact]
    public async Task RevokingWhileQueuedIsRefusedBeforeAnyDriverCall()
    {
        ServiceHarness? harness = null;
        var generationReads = 0;

        harness = ServiceHarness.Create(
            allowed: DesktopCapability.WebViewNavigate,
            hasThreadAccess: true,
            connectionGeneration: () =>
            {
                if (Interlocked.Increment(ref generationReads) == 2)
                {
                    // 第 2 次取世代 = 触碰页面前重建复检请求 ⇒ 此刻撤权 = "排队期间被撤权"。
                    harness!.Service.Authority.RevokeGrant(GrantId);
                }

                return Generation;
            });

        harness.Service.Authority.NotifyConnectionGeneration(Generation);

        var issuedAt = harness.Clock.GetUtcNow();
        harness.Service.Authority.IssueGrant(new BrowserPageGrant(
            GrantId,
            new BrowserPageGrantBinding(
                new DesktopInstanceId("desk-test"), new DesktopProcessInstanceId("proc-test"), Generation),
            ServiceHarness.AgentPage,
            Agent,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            epoch: default,
            issuedAt,
            expiresAtUtc: issuedAt.AddMinutes(5)));

        var context = harness.Context("op-revoked-while-queued") with { BrowserCaller = Agent };

        var response = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            context);

        Assert.True(generationReads >= 2, $"复检必须再取一次世代（实际 {generationReads} 次）");
        Assert.True(response.IsFailure);
        Assert.Equal(0, harness.Surface.NavigateCount);
    }
}