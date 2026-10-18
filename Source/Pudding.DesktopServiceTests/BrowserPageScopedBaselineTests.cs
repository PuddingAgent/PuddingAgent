using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Xunit;

namespace DesktopServiceTests;

/// <summary>
/// 页面级授权基线（ADR-097 D4 / 审阅报告 F3）：**桥在线只证明传输活性，不授予任意页面的控制权**。
///
/// 修复前 <see cref="BrowserPageLifecycle.HasSufficientPageGrant"/> 在
/// <c>Authority.ConnectionGeneration.IsLive</c> 时无条件返回 <c>true</c> ⇒ 同一个浏览器控制器持有的
/// 任意页面都能被 Agent 驱动，取消目标/不选目标都无法拒绝。现在只剩两个来源：
/// ① 用户在 Desktop 上显式选中的 Agent 目标页；③ 页级 grant（能力通道签发）。
/// </summary>
public sealed class BrowserPageScopedBaselineTests
{
    private static readonly DesktopPageTarget PageA = new("ctx-1", "page-a");
    private static readonly DesktopPageTarget PageB = new("ctx-1", "page-b");

    private static readonly BrowserCallerIdentity Caller = new(
        BrowserCallerKind.Agent, "agent-a", "run-agent-a");

    [Fact]
    public void BridgeLivenessAlone_DoesNotAuthorizeAPage()
    {
        var lifecycle = ReadyLifecycle(registerPageA: true);

        Assert.False(
            lifecycle.HasSufficientPageGrant(Caller, PageA, BrowserPageGrantScope.Read, DateTimeOffset.UtcNow),
            "桥在线不等于用户把这一页交给了 Agent（F3）。");
        Assert.False(
            lifecycle.HasSufficientPageGrant(Caller, PageA, BrowserPageGrantScope.Write, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UserSelectedTargetPage_IsAuthorizedWithoutAnyGrant()
    {
        var lifecycle = ReadyLifecycle(registerPageA: true);

        lifecycle.SetAgentTarget(PageA.ContextId, PageA.PageId);

        Assert.True(lifecycle.HasSufficientPageGrant(Caller, PageA, BrowserPageGrantScope.Read, DateTimeOffset.UtcNow));
        Assert.True(lifecycle.HasSufficientPageGrant(Caller, PageA, BrowserPageGrantScope.Write, DateTimeOffset.UtcNow));

        // 同一上下文里的**非目标页**仍然拒绝：页级隔离不能被"上下文可信"取代。
        Assert.False(lifecycle.HasSufficientPageGrant(Caller, PageB, BrowserPageGrantScope.Read, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SwitchingTheTarget_RevokesTheOldPageBaseline()
    {
        var lifecycle = ReadyLifecycle(registerPageA: true);
        lifecycle.RegisterPage(PageB, DesktopPageVersion.Require(4), DesktopPageReadiness.Interactive);
        lifecycle.SetAgentTarget(PageA.ContextId, PageA.PageId);

        Assert.True(lifecycle.HasSufficientPageGrant(Caller, PageA, BrowserPageGrantScope.Read, DateTimeOffset.UtcNow));

        lifecycle.SetAgentTarget(PageB.ContextId, PageB.PageId);

        Assert.False(
            lifecycle.HasSufficientPageGrant(Caller, PageA, BrowserPageGrantScope.Read, DateTimeOffset.UtcNow),
            "切走目标后，旧页不得再依赖目标基线被驱动。");
        Assert.True(lifecycle.HasSufficientPageGrant(Caller, PageB, BrowserPageGrantScope.Read, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RevokingTheTarget_RemovesTheBaseline()
    {
        var lifecycle = ReadyLifecycle(registerPageA: true);
        lifecycle.SetAgentTarget(PageA.ContextId, PageA.PageId);

        lifecycle.SetAgentTarget(null, null);

        Assert.False(lifecycle.HasSufficientPageGrant(Caller, PageA, BrowserPageGrantScope.Read, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void APageGrantStillAuthorizesANonTargetPage()
    {
        var lifecycle = ReadyLifecycle(registerPageA: true);

        // 能力通道签发的页级授权是独立来源（③）：没有用户选页也应放行该页。
        var now = DateTimeOffset.UtcNow;
        lifecycle.Authority.IssueGrant(new BrowserPageGrant(
            new BrowserPageGrantId("grant-1"),
            new BrowserPageGrantBinding(
                new DesktopInstanceId("desk-test"),
                new DesktopProcessInstanceId("proc-test"),
                BrowserTestStateGeneration),
            PageA,
            Caller,
            BrowserPageGrantScope.Read,
            epoch: default,
            now,
            expiresAtUtc: now.AddMinutes(5)));

        Assert.True(lifecycle.HasSufficientPageGrant(Caller, PageA, BrowserPageGrantScope.Read, now));
    }

    private static readonly ConnectionGeneration BrowserTestStateGeneration = ConnectionGeneration.Require(9);

    private static BrowserPageLifecycle ReadyLifecycle(bool registerPageA)
    {
        var lifecycle = BrowserPageLifecycle.Create(
            new DesktopInstanceId("desk-test"),
            new DesktopProcessInstanceId("proc-test"));

        // 桥已确认世代（修复前这一条就足以放行任意页面）。
        lifecycle.NotifyBridgeConnected(BrowserTestStateGeneration.Value);
        lifecycle.RegisterContext(PageA.ContextId, DesktopContextTrust.AgentAuthorized);
        if (registerPageA)
        {
            lifecycle.RegisterPage(PageA, DesktopPageVersion.Require(3), DesktopPageReadiness.Interactive);
        }

        return lifecycle;
    }
}
