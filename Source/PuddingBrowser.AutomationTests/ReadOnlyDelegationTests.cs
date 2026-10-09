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
    public void AnInFlightWriteLeaseIsNeverFreedByTimeAlone()
    {
        // 这条**故意钉住"看起来像缺陷"的安全方向**（round-109 的审计结论）：
        // 权威签发的写租约**不带到期时间**（`expiresAtUtc` 为空），因此"过了很久"不会让它消失。
        //
        // 为什么这是对的、不能"顺手修成按时间清理"：清理后必须立刻允许**另一个写入者**动手，
        // 而我们无法知道原持有者是否已经停止 ⇒ 会变成**两个写入者同时操作同一页**（正是 G10 要防的）。
        // 因此安全方向只能是：时间不释放；释放只来自显式 ReleaseLease / 暂停 / 接管 / 世代改变。
        var clock = new TestTimeProvider(B0Fixture.Now);
        var authority = B0Fixture.Connected(clock);
        var page = B0Fixture.Page();

        var actorA = B0Fixture.Agent("agent-1", "task-1");
        var actorB = B0Fixture.Agent("agent-2", "task-2");
        authority.IssueGrant(B0Fixture.Grant(actorA, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));
        authority.IssueGrant(B0Fixture.Grant(actorB, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));

        var leaseA = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, actorA, target: page));
        Assert.IsTrue(leaseA.IsAllowed);

        // ① 时间过去了很久：租约**不**因时间而释放 —— B 仍被挡（宁可让宿主显式释放，也不能放两个写入者）。
        clock.Advance(TimeSpan.FromHours(6));
        var tooEarly = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, actorB, target: page));
        Assert.IsFalse(tooEarly.IsAllowed, "时间流逝不得让在途写租约失效（否则会出现两个写入者）");

        // ② 显式释放后，B 立刻可以写。
        Assert.IsNotNull(leaseA.Lease);
        authority.ReleaseLease(leaseA.Lease!.LeaseId, "test");
        var afterRelease = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, actorB, target: page));
        Assert.IsTrue(afterRelease.IsAllowed, "显式释放必须真正让出该页");

        // ③ 暂停同样释放（控制面无须知道租约 id）。
        clock.Advance(TimeSpan.FromMinutes(1));
        authority.SetPaused(true, "test");
        authority.SetPaused(false, "test");
        var actorC = B0Fixture.Agent("agent-3", "task-3");
        authority.IssueGrant(B0Fixture.Grant(actorC, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));
        var afterPause = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, actorC, target: page));
        Assert.IsTrue(afterPause.IsAllowed, "暂停必须释放租约");
    }

    [TestMethod]
    public void AWriteLeaseIsReleasedWhenItsOwnGrantIsRevoked_EvenIfAnotherActorSharesTheCallerId()
    {
        // 取红用例（round-108）：租约存活性检查（`HasWriteCoverage`）原先也只比 `CallerId` 字符串。
        // 于是当**另一个同 id 的行动者**在该页上还有写授权时，我的授权被撤掉后租约**不释放**：
        // 违反"租约不得比它的授权活得久"，并且会以 PageBusy 长期挡住该页的其它写入者。
        // 这里用公开行为观察：撤掉 A 的授权后，B 必须能立刻拿到写租约。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();

        var actorA = B0Fixture.Agent("agent-1", "task-1");
        var actorB = B0Fixture.Agent("agent-1", "task-2"); // 同一个 CallerId，不同任务

        var grantA = B0Fixture.Grant(actorA, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page);
        authority.IssueGrant(grantA);
        authority.IssueGrant(B0Fixture.Grant(actorB, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));

        // A 拿到写租约（在途）。
        var leaseA = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, actorA, target: page));
        Assert.IsTrue(leaseA.IsAllowed, "A 应当拿到写租约");

        // 只撤掉 A 的授权（B 的仍在）。
        authority.RevokeGrant(grantA.GrantId);

        // B 必须能立刻写：这只有在"A 的租约真的被释放"时才成立。
        var leaseB = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, actorB, target: page));
        Assert.IsTrue(
            leaseB.IsAllowed,
            "撤掉 A 的授权后，A 的在途租约必须释放（否则该页被一个已无授权的租约长期占住）");
    }

    [TestMethod]
    public void ReissuingAGrantIdWithDifferentContentIsRefusedInsteadOfSilentlyWidening()
    {
        // 取红用例（round-107 修的真实静默提权）：`IssueGrant` 原先按 GrantId **直接覆盖**。
        // 覆盖 = 在同一个授权世代内改变授权内容 ⇒ 已签发租约仍有效（Revalidate 查 epoch），
        // 于是"同 id 换一份更宽的授权"能绕过世代检查。授权记录声明是不可变的，就必须拒绝而不是覆盖。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();
        var grantId = BrowserPageGrantId.NewId();

        var narrow = new BrowserPageGrant(
            grantId,
            B0Fixture.Binding(),
            page,
            B0Fixture.Agent(),
            BrowserPageGrantScope.Read,
            BrowserPageGrantEpoch.Initial,
            B0Fixture.Now);
        authority.IssueGrant(narrow);

        // ① 原样重发是幂等的（宿主可能重试同一签发）。
        authority.IssueGrant(narrow);
        Assert.AreEqual(1, authority.ActiveGrants.Count);

        // ② 同 id 换内容（这里是把只读扩成读写）必须被拒绝。
        var widened = new BrowserPageGrant(
            grantId,
            B0Fixture.Binding(),
            page,
            B0Fixture.Agent(),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            BrowserPageGrantEpoch.Initial,
            B0Fixture.Now);
        Assert.ThrowsExactly<ArgumentException>(() => authority.IssueGrant(widened));

        // ③ 而且**旧记录仍按原样生效**：写操作依然不被允许（没被偷偷扩权）。
        var write = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Interact, B0Fixture.Agent(), target: page));
        Assert.IsFalse(write.IsAllowed, "拒绝替换之后，原记录的窄范围必须仍然生效");
        Assert.AreEqual(BrowserPageGrantScope.Read, authority.ActiveGrants.Single().Scope);
    }

    [TestMethod]
    public void ReissuingAManagementGrantIdWithDifferentContentIsRefused()
    {
        var authority = B0Fixture.Connected();
        var grantId = BrowserManagementGrantId.NewId();

        var first = new BrowserManagementGrant(
            grantId, B0Fixture.Binding(), B0Fixture.Agent(), B0Fixture.Now);
        authority.IssueManagementGrant(first);
        authority.IssueManagementGrant(first); // 幂等

        var second = new BrowserManagementGrant(
            grantId, B0Fixture.Binding(), B0Fixture.Agent("agent-2", "task-2"), B0Fixture.Now);
        Assert.ThrowsExactly<ArgumentException>(() => authority.IssueManagementGrant(second));

        Assert.AreEqual(1, authority.ActiveManagementGrants.Count);
    }

    [TestMethod]
    public void APageGrantIsBoundToTheExactActorAndNotJustToACallerIdString()
    {
        // 取红用例（round-106 修的真实越权借用）：页面授权的匹配原先只比 `CallerId` 字符串。
        // CallerId 只是字符串 ⇒ 种类不同的调用者（子代理）或同一调用者在另一个任务里，
        // 只要 id 长得一样就能用**别人的**页面授权。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();

        authority.IssueGrant(B0Fixture.Grant(
            B0Fixture.Agent("agent-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page));

        // ① 同 id 但**不同种类**（子代理）：不得借用父授权。
        var asSubAgent = B0Fixture.Request(
            BrowserAutomationOperation.Snapshot,
            new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "task-sub", "task-1"),
            target: page);
        Assert.IsFalse(
            authority.Admit(asSubAgent).IsAllowed,
            "子代理不能仅凭 CallerId 相同就借用父任务的页面授权");

        // ② 同种类同 id 但**不同任务**：授权发给某个任务，不能跨任务复用。
        var otherTask = B0Fixture.Request(
            BrowserAutomationOperation.Snapshot,
            B0Fixture.Agent("agent-1", "task-2"),
            target: page);
        Assert.IsFalse(
            authority.Admit(otherTask).IsAllowed,
            "页面授权不得跨任务复用（同一调用者在另一个任务里也不行）");

        // ③ 精确的行动者仍然放行（防止修得过头）。
        var sameActor = B0Fixture.Request(
            BrowserAutomationOperation.Snapshot,
            B0Fixture.Agent("agent-1", "task-1"),
            target: page);
        Assert.IsTrue(authority.Admit(sameActor).IsAllowed, "同一个行动者必须仍被放行");
    }

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
