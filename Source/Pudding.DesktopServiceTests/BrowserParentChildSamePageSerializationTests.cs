using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace DesktopServiceTests;

/// <summary>
/// B6（§5）：**父子不并发同页写**。父代理的同页写在途时，子代理的写必须被拒（PageBusy）且**不产生驱动调用**；
/// 父操作结束后（租约释放）子代理才能取得写入。判据落在唯一权威的"每页一份写租约"上（与调用者是谁无关）。
/// </summary>
public sealed class BrowserParentChildSamePageSerializationTests
{
    private static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(51);
    private static readonly BrowserCallerIdentity Parent = new(BrowserCallerKind.Agent, "agent-1", "run-parent");
    private static readonly BrowserCallerIdentity Child =
        new(BrowserCallerKind.SubAgent, "agent-1", "run-child", parentTaskId: "run-parent");

    private static ServiceHarness Harness() => ServiceHarness.Create(
        allowed: DesktopCapability.WebViewNavigate,
        hasThreadAccess: true,
        connectionGeneration: () => Generation);

    private static void IssueGrant(ServiceHarness harness, BrowserCallerIdentity grantee)
    {
        var issuedAt = harness.Clock.GetUtcNow();
        harness.Service.Authority.IssueGrant(new BrowserPageGrant(
            new BrowserPageGrantId($"grant-{grantee.TaskId}"),
            new BrowserPageGrantBinding(
                new DesktopInstanceId("desk-test"), new DesktopProcessInstanceId("proc-test"), Generation),
            ServiceHarness.AgentPage,
            grantee,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            epoch: default,
            issuedAt,
            expiresAtUtc: issuedAt.AddMinutes(5)));
    }

    private static DesktopCallContext Context(ServiceHarness harness, string operationId, BrowserCallerIdentity caller) =>
        harness.Context(operationId) with { BrowserCaller = caller };

    [Fact]
    public async Task AChildWriteIsRefusedWhileTheParentWriteOnTheSamePageIsInFlight()
    {
        var harness = Harness();
        harness.Service.Authority.NotifyConnectionGeneration(Generation);
        IssueGrant(harness, Parent);
        IssueGrant(harness, Child);

        // 让父操作"卡"在 surface 里 ⇒ 父持有该页的写租约。
        harness.Surface.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var parentCall = harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            Context(harness, "op-parent", Parent));

        // 等父操作真的进到驱动（surface 在等 gate 之前就会登记调用）。
        var waited = 0;
        while (harness.Surface.NavigateCount < 1 && waited < 200)
        {
            await Task.Delay(10);
            waited++;
        }

        Assert.Equal(1, harness.Surface.NavigateCount);

        var childCall = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            Context(harness, "op-child-blocked", Child));

        // ① 子代理被拒；② 没有第二次驱动调用（拒绝发生在触碰页面之前）。
        Assert.True(childCall.IsFailure, "父在途时子代理的同页写必须被拒");
        Assert.Equal(1, harness.Surface.NavigateCount);

        // 放行父操作 ⇒ 租约释放 ⇒ 子代理现在可以写。
        harness.Surface.Gate.SetResult();
        var parentCallResult = await parentCall;
        Assert.False(parentCallResult.IsFailure);

        harness.Surface.Gate = null;
        var childRetry = await harness.ExecuteAsync(
            DesktopCapability.WebViewNavigate,
            harness.NavigateRequest(ServiceHarness.AgentPage),
            Context(harness, "op-child-after", Child));

        Assert.False(childRetry.IsFailure);
        Assert.Equal(2, harness.Surface.NavigateCount);
    }
}