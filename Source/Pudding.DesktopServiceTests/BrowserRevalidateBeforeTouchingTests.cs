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
        // 排队期间撤权必须在触碰 driver 之前被拒。
        // 机制在 2026-10-12 复核后**变了**：旧实现靠"复检时再取一次世代 provider"来发现变动，
        // 而方案 §3.1/§3.2 明令 UI 回调**不得**用"当前世代 provider"重建请求，改由**准入世代比对**
        // （BrowserControlSnapshot.ControlEpoch / GrantEpoch）负责。断言的不变量不变，机制换新。
        ServiceHarness? harness = null;

        harness = ServiceHarness.Create(
            allowed: DesktopCapability.WebViewNavigate,
            hasThreadAccess: false,
            connectionGeneration: () => Generation);

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

        var pending = harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            context);

        Assert.False(pending.IsCompleted); // 先确认它真的排在队列里

        harness.Service.Authority.RevokeGrant(GrantId); // 排队期间撤权 ⇒ 授权世代推进
        harness.Dispatcher.Pump();

        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(
            response.IsFailure,
            $"revoked-while-queued operation must be refused; driver calls={harness.Surface.NavigateCount}");
        Assert.Equal(0, harness.Surface.NavigateCount);
        Assert.Null(harness.Service.Authority.Capture().ActiveLease); // 且不得残留租约
    }
}