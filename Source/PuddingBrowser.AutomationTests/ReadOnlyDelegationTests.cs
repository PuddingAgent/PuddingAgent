using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **只读委派的组件级端到端**（[B6 接线拆解](../../../Docs/12_features/B6-接线可执行拆解-2026-10-08.md) 的 S5）。
///
/// 单独看每个组件都有用例（权威状态机、租约账本、信封校验），但"只读委派"这条**组合路径**此前没有：
/// 而它正是 Runtime 接线（S7）要调的形状。这里用纯内存对象把整条链路走通：
/// 父任务授权 → 子代理**只读**授权（必须是父授权的子集）→ 只读委派观察 →
/// 子代理写请求被拒 → 关闭委派（超时）后观察被回收、授权撤销、此后写入仍被拒。
/// </summary>
[TestClass]
public sealed class ReadOnlyDelegationTests
{
    [TestMethod]
    public void AReadOnlySubAgentGrantIsAllowedAndItsWritesAreDenied()
    {
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();
        var parent = B0Fixture.Grant(B0Fixture.Agent());
        authority.IssueGrant(parent);

        // 子代理只读授权：必须是父授权在该目标上的**子集**（组件强制，不是文档约定）。
        var child = B0Fixture.Grant(
            B0Fixture.SubAgent(), BrowserPageGrantScope.Read, target: page);
        authority.IssueGrant(child);

        var read = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Snapshot, B0Fixture.SubAgent(), target: page));
        Assert.IsTrue(read.IsAllowed, $"只读委派必须能读：{read.Denial}");

        var write = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Interact, B0Fixture.SubAgent(), target: page));
        Assert.IsFalse(write.IsAllowed, "只读委派的写请求必须被拒（否则'只读'只是装饰）");
    }

    [TestMethod]
    public void ASubAgentGrantCannotExceedItsParentScope()
    {
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();

        // 父任务只有只读授权。
        authority.IssueGrant(B0Fixture.Grant(B0Fixture.Agent(), BrowserPageGrantScope.Read, target: page));

        // 子代理想要读写 —— 超过父范围，签发阶段就必须失败（fail closed，不能等到运行时才拦）。
        var tooWide = B0Fixture.Grant(
            B0Fixture.SubAgent(), BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page);

        Assert.ThrowsExactly<ArgumentException>(() => authority.IssueGrant(tooWide));
    }

    [TestMethod]
    public void AReadOnlyDelegationDoesNotBlockTheParentFromWriting()
    {
        // 这条是"只读"委派真正的产品价值：子代理在观察，父代理仍必须能写（否则委派会把父任务锁死）。
        var ledger = new BrowserDelegationLeaseLedger();

        Assert.IsTrue(ledger.TryAcquireRead("page-1", "sub-1", "delegation-1").Granted);
        Assert.IsTrue(
            ledger.TryAcquireWrite("page-1", "parent", BrowserLeaseRole.Parent).Granted,
            "只读观察不得阻塞父代理的写租约");
        Assert.IsTrue(ledger.TryAcquireRead("page-1", "parent").Granted, "父代理同时也能只读观察");

        // 关闭委派（超时）：子代理的观察被回收，父代理的写租约**不受影响**。
        ledger.EndDelegation("delegation-1", BrowserLeaseReleaseReason.TimedOut);

        Assert.AreEqual(1, ledger.ReaderCountOf("page-1"), "子代理的观察必须被回收，父代理的留下");
        Assert.AreEqual("parent", ledger.WriteHolderOf("page-1"));
        Assert.AreEqual(BrowserLeaseReleaseReason.TimedOut, ledger.RecentReleases[^1].Reason);
    }

    [TestMethod]
    public void ClosingAReadOnlyDelegationRevokesTheChildGrantSoLaterWritesStayDenied()
    {
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();
        authority.IssueGrant(B0Fixture.Grant(B0Fixture.Agent()));
        var child = B0Fixture.Grant(B0Fixture.SubAgent(), BrowserPageGrantScope.Read, target: page);
        authority.IssueGrant(child);

        authority.RevokeGrant(child.GrantId);

        var write = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Interact, B0Fixture.SubAgent(), target: page));
        Assert.IsFalse(write.IsAllowed, "委派关闭后子代理的写请求必须仍然被拒");

        var read = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Snapshot, B0Fixture.SubAgent(), target: page));
        Assert.IsFalse(read.IsAllowed, "委派关闭后连只读也不该继续授权");
    }
}
