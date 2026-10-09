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
    public void ASubAgentGrantCannotBeIssuedUnderAnExpiredParentGrant()
    {
        // 取红用例（round-115）：子集判定只看"同任务+同页+同 frame+范围"，**不看父授权是否过期** ⇒
        // 父授权过期后仍能签出子代理授权，而子代理自己的授权是有效的 ⇒ 凭一个**已经死掉的来源**获得权限。
        var clock = new TestTimeProvider(B0Fixture.Now);
        var authority = B0Fixture.Connected(clock);
        var page = B0Fixture.Page();

        authority.IssueGrant(B0Fixture.Grant(
            B0Fixture.Agent("agent-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page,
            lifetime: TimeSpan.FromMinutes(5)));

        clock.Advance(TimeSpan.FromMinutes(6)); // 父授权已过期

        var child = B0Fixture.Grant(
            B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1"), BrowserPageGrantScope.Read, target: page);
        Assert.ThrowsExactly<ArgumentException>(() => authority.IssueGrant(child));
    }

    [TestMethod]
    public void ExpiredGrantsAreReclaimedSoTheDictionaryDoesNotGrowForever()
    {
        // 已知取舍收口：过期记录既不被准入认可，也不该无限留在字典里。
        // 可观察证据：记录被回收后，**同一个 GrantId 可以重新签发**（旧的死记录已不在，不再触发"不可变"拒绝）。
        var clock = new TestTimeProvider(B0Fixture.Now);
        var authority = B0Fixture.Connected(clock);
        var page = B0Fixture.Page();
        var grantId = BrowserPageGrantId.NewId();
        var actor = B0Fixture.Agent("agent-1", "task-1");

        authority.IssueGrant(new BrowserPageGrant(
            grantId, B0Fixture.Binding(), page, actor,
            BrowserPageGrantScope.Read, BrowserPageGrantEpoch.Initial, B0Fixture.Now,
            null, B0Fixture.Now + TimeSpan.FromMinutes(5)));

        clock.Advance(TimeSpan.FromMinutes(6));

        // 触发回收（签发是唯一的增长点，因此回收也放在那里）。
        authority.IssueGrant(new BrowserPageGrant(
            BrowserPageGrantId.NewId(), B0Fixture.Binding(), page, actor,
            BrowserPageGrantScope.Read, BrowserPageGrantEpoch.Initial, clock.GetUtcNow()));

        // 再签发同一 GrantId：若能成功，说明旧的过期记录确实被回收了（否则会因"不可变"被拒）。
        authority.IssueGrant(new BrowserPageGrant(
            grantId, B0Fixture.Binding(), page, actor,
            BrowserPageGrantScope.Read, BrowserPageGrantEpoch.Initial, clock.GetUtcNow()));

        Assert.AreEqual(2, authority.ActiveGrants.Count, "回收后字典里只应剩两条有效授权");
    }

    [TestMethod]
    public void ExpiredGrantsDisappearFromTheActiveProjections_SoTheProjectionAgreesWithAdmission()
    {
        // 取红用例（round-113 修的真实缺陷）：`ActiveGrants` / `ActiveManagementGrants` 原先直接返回
        // 字典内容**不过滤过期**，而契约说的是"当前**有效**的授权记录"。后果：UI 把已经被准入拒绝的授权
        // 算作在册 —— 投影与准入说法不一致（用户看到"还有授权"，实际一调就拒）。
        var clock = new TestTimeProvider(B0Fixture.Now);
        var authority = B0Fixture.Connected(clock);
        var page = B0Fixture.Page();

        var actor = B0Fixture.Agent("agent-1", "task-1");
        authority.IssueGrant(B0Fixture.Grant(
            actor,
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page,
            lifetime: TimeSpan.FromMinutes(5)));
        authority.IssueManagementGrant(B0Fixture.ManagementGrant(actor, lifetime: TimeSpan.FromMinutes(5)));

        // 未过期：投影里能看到，准入也允许 —— 两者一致。
        Assert.AreEqual(1, authority.ActiveGrants.Count);
        Assert.AreEqual(1, authority.ActiveManagementGrants.Count);
        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, actor, target: page)).IsAllowed);

        clock.Advance(TimeSpan.FromMinutes(6));

        // 过期后：投影必须为空（**与准入一致**），否则 UI 会报告一个不存在的授权。
        Assert.IsEmpty(authority.ActiveGrants, "过期授权不得出现在'当前有效授权'投影里");
        Assert.IsEmpty(authority.ActiveManagementGrants, "过期管理授权同样不得出现");
        Assert.IsFalse(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, actor, target: page)).IsAllowed,
            "过期授权本来就不被准入认可（这两条必须一致）");
    }

    [TestMethod]
    public void RevokingAManagementGrantKeepsTheInFlightPageLease()
    {
        // 对账表缺口 #1：把"撤管理授权**不**释放在途页面写租约"这一**有意**语义钉住
        // （撤管理授权只影响"能否创建/关闭目标"，不该打断已经开始的页面写）。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();
        var actor = B0Fixture.Agent("agent-1", "task-1");
        authority.IssueGrant(B0Fixture.Grant(actor, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));
        var management = B0Fixture.ManagementGrant(actor);
        authority.IssueManagementGrant(management);

        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, actor, target: page)).IsAllowed);

        authority.RevokeManagementGrant(management.GrantId);

        Assert.IsEmpty(authority.ActiveManagementGrants);

        // ① 管理类操作立刻不可用（管理授权确实被撤）。
        Assert.IsFalse(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.ContextCreate, actor)).IsAllowed,
            "撤管理授权后，管理类操作必须被拒");

        // ② 页面写在途租约**仍在**：另一个行动者被 PageBusy 挡住（若租约被错误释放，它会立刻拿到许可）。
        var other = B0Fixture.Agent("agent-2", "task-2");
        authority.IssueGrant(B0Fixture.Grant(other, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));
        Assert.IsFalse(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, other, target: page)).IsAllowed,
            "撤管理授权不得释放在途页面写租约（有意语义）");
    }

    [TestMethod]
    public void CaptureIsSideEffectFree()
    {
        // 对账表缺口 #5：读取面不得改变任何状态（捕获是只读投影）。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();
        authority.IssueGrant(B0Fixture.Grant(
            B0Fixture.Agent("agent-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page));

        var first = authority.Capture();
        var second = authority.Capture();
        var third = authority.Capture();

        Assert.AreEqual(first.ControlEpoch.Value, second.ControlEpoch.Value);
        Assert.AreEqual(first.GrantEpoch.Value, second.GrantEpoch.Value);
        Assert.AreEqual(second.ControlEpoch.Value, third.ControlEpoch.Value);
        Assert.AreEqual(second.GrantEpoch.Value, third.GrantEpoch.Value);
        Assert.AreEqual(1, authority.ActiveGrants.Count, "读取不得改变授权集合");
    }

    [TestMethod]
    public void ConcurrentAdmissionNeverGrantsTwoWritersOnOnePage()
    {
        // 对账表缺口 #6（最大未覆盖面）：**有界**并发冒烟。不追求证明线程安全，
        // 只证明"并发下也不会出现两个写租约"这一条最要命的不变量，并确保不抛异常。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();
        var actors = Enumerable.Range(0, 8)
            .Select(index => B0Fixture.Agent($"agent-{index}", $"task-{index}"))
            .ToArray();
        foreach (var actor in actors)
        {
            authority.IssueGrant(B0Fixture.Grant(
                actor, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));
        }

        var allowed = 0;
        var failures = new List<Exception>();
        Parallel.ForEach(actors, actor =>
        {
            try
            {
                if (authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, actor, target: page)).IsAllowed)
                {
                    Interlocked.Increment(ref allowed);
                }
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(exception);
                }
            }
        });

        Assert.IsEmpty(failures, "并发准入不得抛异常");
        // 关键不变量：同一页同一时刻只有一个写租约 ⇒ 最多一个并发请求拿到许可。
        Assert.AreEqual(1, allowed, "同一页不得出现两个写租约（并发下也成立）");
    }

    [TestMethod]
    public void RevokeAllGrantsClearsDerivedGrantsAndReleasesTheInFlightLease()
    {
        // 对账表缺口 #3：`RevokeAllGrants` 只验证过"正向清空"。这里连派生授权与**在途租约**一起看：
        // 清空后又重新签发一条授权，如果旧租约还在，新写入者会拿到 PageBusy（这就是可观察的证据）。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();
        var parent = B0Fixture.Grant(
            B0Fixture.Agent("agent-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page);
        authority.IssueGrant(parent);
        authority.IssueGrant(B0Fixture.Grant(
            B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page));
        authority.IssueGrant(B0Fixture.Grant(
            B0Fixture.SubAgent("sub-2", "task-sub-2", "task-sub-1"),
            BrowserPageGrantScope.Read,
            target: page));
        authority.IssueManagementGrant(B0Fixture.ManagementGrant(B0Fixture.Agent("agent-1", "task-1")));

        var child = B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1");
        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, child, target: page)).IsAllowed);

        authority.RevokeAllGrants();

        Assert.IsEmpty(authority.ActiveGrants, "父/子/孙授权都必须被清空");
        Assert.IsEmpty(authority.ActiveManagementGrants);
        Assert.IsFalse(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, child, target: page)).IsAllowed,
            "清空后子代理不得再写");

        // 重新签发一条新授权：若旧租约没被释放，这里会得到 PageBusy。
        var fresh = B0Fixture.Agent("agent-9", "task-9");
        authority.IssueGrant(B0Fixture.Grant(fresh, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));
        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, fresh, target: page)).IsAllowed,
            "RevokeAllGrants 必须同时释放在途租约（否则该页被旧租约占住）");
    }

    [TestMethod]
    public void RevokeExecutionScopeNarrowsSubAgentGrantsAndReleasesTheirInFlightLease()
    {
        // 对账表缺口 #2：`RevokeExecutionScope` 只验证过"去掉 Write 保留 Read"的正向行为。
        // 这里看两件更要紧的事：① 子代理的授权是否**一起收窄**（子集关系不被破坏）；
        // ② 子代理的在途写租约是否释放（用一个新写入者观察，避免只看内部状态）。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();
        authority.IssueGrant(B0Fixture.Grant(
            B0Fixture.Agent("agent-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page));
        var child = B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1");
        authority.IssueGrant(B0Fixture.Grant(
            child, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));

        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, child, target: page)).IsAllowed);

        authority.RevokeExecutionScope(page);

        Assert.IsFalse(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, child, target: page)).IsAllowed,
            "撤执行权必须连子代理的写权一起收窄（否则子代理可以绕过父任务的收缩）");
        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, child, target: page)).IsAllowed,
            "只读授权应当保留");

        var fresh = B0Fixture.Agent("agent-9", "task-9");
        authority.IssueGrant(B0Fixture.Grant(fresh, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, target: page));
        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, fresh, target: page)).IsAllowed,
            "撤执行权必须释放在途写租约（否则该页被旧租约占住）");
    }

    [TestMethod]
    public void RevokingTheParentGrantAlsoRevokesTheDerivedSubAgentGrants()
    {
        // 取红用例（round-110）：签发时强制"子代理只能是父授权的子集"，但**撤销父授权时只删父记录** ⇒
        // 子代理带着"来源已消失"的授权继续写。授权链断了，派生授权必须一起失效（fail closed）。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();

        var parent = B0Fixture.Grant(
            B0Fixture.Agent("agent-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page);
        authority.IssueGrant(parent);

        var child = B0Fixture.Grant(
            B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page);
        authority.IssueGrant(child);

        // 父授权还在时，子代理可以用自己的（子集）授权写。
        var before = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Interact, B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1"), target: page));
        Assert.IsTrue(before.IsAllowed, "父授权还在时，子代理的合法子集授权应当可用");

        // 父授权被撤销。
        authority.RevokeGrant(parent.GrantId);

        var after = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Interact, B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1"), target: page));
        Assert.IsFalse(
            after.IsAllowed,
            "父授权撤销后，派生的子代理授权必须一起失效（否则授权链断裂而权限仍在）");

        // 只读也一样：不是"只收写权"。
        var readAfter = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Snapshot, B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1"), target: page));
        Assert.IsFalse(readAfter.IsAllowed, "父授权撤销后子代理的读授权同样不该存活");
    }

    [TestMethod]
    public void RevokingTheParentGrantCascadesThroughGrandchildren()
    {
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();

        var parent = B0Fixture.Grant(
            B0Fixture.Agent("agent-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page);
        authority.IssueGrant(parent);

        var child = B0Fixture.Grant(
            B0Fixture.SubAgent("sub-1", "task-sub-1", "task-1"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page);
        authority.IssueGrant(child);

        // 孙代理：父任务是子代理的任务（"task-sub-1"）。
        authority.IssueGrant(B0Fixture.Grant(
            B0Fixture.SubAgent("sub-2", "task-sub-2", "task-sub-1"),
            BrowserPageGrantScope.Read,
            target: page));

        authority.RevokeGrant(parent.GrantId);

        var grandChild = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Snapshot, B0Fixture.SubAgent("sub-2", "task-sub-2", "task-sub-1"), target: page));
        Assert.IsFalse(grandChild.IsAllowed, "级联必须一路清到孙代理");
        Assert.IsEmpty(authority.ActiveGrants, "父、子、孙三条授权都应被清除");
    }

    [TestMethod]
    public void RevokingOneTasksGrantDoesNotTouchAnotherTasksSubAgents()
    {
        // 反向保护：级联不能伤及**别的任务**派生的子代理授权。
        var authority = B0Fixture.Connected();
        var page = B0Fixture.Page();

        var parentA = B0Fixture.Grant(
            B0Fixture.Agent("agent-a", "task-a"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page);
        var parentB = B0Fixture.Grant(
            B0Fixture.Agent("agent-b", "task-b"),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            target: page);
        authority.IssueGrant(parentA);
        authority.IssueGrant(parentB);

        var childA = B0Fixture.Grant(
            B0Fixture.SubAgent("sub-a", "task-sub-a", "task-a"), BrowserPageGrantScope.Read, target: page);
        var childB = B0Fixture.Grant(
            B0Fixture.SubAgent("sub-b", "task-sub-b", "task-b"), BrowserPageGrantScope.Read, target: page);
        authority.IssueGrant(childA);
        authority.IssueGrant(childB);

        authority.RevokeGrant(parentA.GrantId);

        var surviving = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Snapshot, B0Fixture.SubAgent("sub-b", "task-sub-b", "task-b"), target: page));
        Assert.IsTrue(surviving.IsAllowed, "别的任务派生的子代理授权不得被牵连");
    }

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
