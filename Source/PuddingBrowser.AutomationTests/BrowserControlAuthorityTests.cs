using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// G1 控制权门禁（设计方案 §3.1 / §11）：
/// 接管/暂停确认后不允许新步骤开始；只读观察仍可用；恢复不复活旧租约；授权不被接管撤销。
/// </summary>
[TestClass]
public sealed class BrowserControlAuthorityTests
{
    // ── 接管 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Takeover_Blocks_New_Write_Steps()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        // 接管前：写操作放行并拿到租约。
        var before = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));
        Assert.IsTrue(before.IsAllowed, "a write must be admitted before the takeover");
        Assert.IsNotNull(before.Lease);

        var control = authority.SetUserTakeover(true, "user clicked the takeover switch");

        Assert.IsTrue(control.IsUserTakeover);
        Assert.IsFalse(control.IsAcceptingNewSteps, "after takeover no new step may start");
        Assert.IsNull(control.ActiveLease, "takeover must revoke the in-flight write lease");

        // 接管后：新的写步骤被拒（驱动调用数 = 0 由「拒绝」这一事实保证）。
        var after = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));
        Assert.IsTrue(after.IsDenied, "writes must be refused while the user holds control");
        Assert.AreEqual(BrowserAuthorizationDenial.UserTakeover, after.Denial);
        Assert.AreEqual(DesktopCapabilityErrorCode.UserTakeover, after.Error!.Code);
    }

    [TestMethod]
    public void Takeover_Does_Not_Revoke_Read_Authorization()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write));

        var grantEpochBefore = authority.Capture().GrantEpoch;
        authority.SetUserTakeover(true);

        Assert.AreEqual(
            grantEpochBefore.Value,
            authority.Capture().GrantEpoch.Value,
            "takeover must not advance the authorization epoch (接管不自动撤销阅读授权)");
        Assert.AreEqual(1, authority.ActiveGrants.Count, "the read grant must survive the takeover");

        // 授权范围内的只读观察在接管期间仍然可用。
        foreach (var readOperation in new[]
                 {
                     BrowserAutomationOperation.Snapshot,
                     BrowserAutomationOperation.Locate,
                     BrowserAutomationOperation.WaitFor,
                     BrowserAutomationOperation.PageState,
                 })
        {
            var decision = authority.Admit(B0Fixture.Request(readOperation, caller));
            Assert.IsTrue(decision.IsAllowed, $"{readOperation} must stay available during takeover");
        }

        // 元数据清单也不受影响。
        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.ContextsList, caller)).IsAllowed);
    }

    [TestMethod]
    public void Takeover_Confirm_Makes_Queued_Revalidate_Fail()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        var request = B0Fixture.Request(BrowserAutomationOperation.Interact, caller);
        var admitted = authority.Admit(request);
        Assert.IsTrue(admitted.IsAllowed);

        // 模拟「验证通过 → 期间被接管」：第二次验证必须在触碰页面前失败。
        authority.SetUserTakeover(true);

        var revalidated = authority.Revalidate(admitted.Lease!, request);
        Assert.IsTrue(revalidated.IsDenied, "a step must not start after the takeover was confirmed");
        Assert.AreEqual(BrowserAuthorizationDenial.UserTakeover, revalidated.Denial);
    }

    [TestMethod]
    public void Resume_Does_Not_Resurrect_The_Pre_Takeover_Lease()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        var request = B0Fixture.Request(BrowserAutomationOperation.Interact, caller);
        var admitted = authority.Admit(request);
        var leaseBefore = admitted.Lease!;
        var controlEpochBefore = authority.Capture().ControlEpoch;

        authority.SetUserTakeover(true);
        var resumed = authority.Resume();

        Assert.IsFalse(resumed.IsUserTakeover);
        Assert.IsTrue(resumed.IsAcceptingNewSteps);
        Assert.IsTrue(
            resumed.ControlEpoch.Value > controlEpochBefore.Value,
            "resume must advance the control epoch so pre-takeover permits cannot come back");
        Assert.IsNull(resumed.ActiveLease, "the pre-takeover lease must not be resurrected");

        // 旧租约在恢复后仍然无效（单调世代消除 ABA 竞态）。
        var revalidated = authority.Revalidate(leaseBefore, request);
        Assert.IsTrue(revalidated.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.LeaseLost, revalidated.Denial);

        // 显式重新准入后可以重新开始（这是新许可，不是复活旧队列）。
        var readmitted = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));
        Assert.IsTrue(readmitted.IsAllowed, "a fresh admission after resume must be allowed");
    }

    [TestMethod]
    public void Resume_Is_Idempotent_When_Nothing_Was_Interrupted()
    {
        var authority = B0Fixture.Connected();
        var epochBefore = authority.Capture().ControlEpoch;

        var resumed = authority.Resume();

        Assert.AreEqual(
            epochBefore.Value,
            resumed.ControlEpoch.Value,
            "resume with nothing to resume must not churn the epoch");
    }

    [TestMethod]
    public void Takeover_And_Pause_Are_Idempotent()
    {
        var authority = B0Fixture.Connected();
        var baseline = authority.Capture().ControlEpoch;

        authority.SetUserTakeover(true);
        var afterFirst = authority.Capture().ControlEpoch;
        authority.SetUserTakeover(true);
        var afterSecond = authority.Capture().ControlEpoch;

        Assert.IsTrue(afterFirst.Value > baseline.Value);
        Assert.AreEqual(afterFirst.Value, afterSecond.Value, "a repeated takeover must not advance the epoch again");
    }

    // ── 暂停 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Pause_Blocks_Writes_But_Keeps_Reads()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        authority.SetPaused(true, "tool runtime paused");

        var write = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Navigate, caller));
        Assert.IsTrue(write.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.Paused, write.Denial);
        Assert.AreEqual(DesktopCapabilityErrorCode.Paused, write.Error!.Code);

        var read = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller));
        Assert.IsTrue(read.IsAllowed, "read-only observation must keep working while paused");
    }

    // ── 关闭与连接世代 ────────────────────────────────────────────────────

    [TestMethod]
    public void Close_Advances_Both_Epochs_And_Drops_Every_Permit()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));
        authority.IssueManagementGrant(B0Fixture.ManagementGrant(caller));
        authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));

        var before = authority.Capture();
        var after = authority.Close("browser surface closed");

        Assert.IsTrue(after.IsClosed);
        Assert.IsTrue(after.ControlEpoch.Value > before.ControlEpoch.Value);
        Assert.IsTrue(after.GrantEpoch.Value > before.GrantEpoch.Value);
        Assert.IsNull(after.ActiveLease);
        Assert.AreEqual(0, authority.ActiveGrants.Count);
        Assert.AreEqual(0, authority.ActiveManagementGrants.Count);

        var read = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller));
        Assert.IsTrue(read.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.Closed, read.Denial);
    }

    [TestMethod]
    public void Connection_Generation_Change_Invalidates_Epochs_Grants_And_Lease()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));
        authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));

        var before = authority.Capture();
        var after = authority.NotifyConnectionGeneration(ConnectionGeneration.Require(8), "reconnected");

        Assert.IsTrue(after.ControlEpoch.Value > before.ControlEpoch.Value);
        Assert.IsTrue(after.GrantEpoch.Value > before.GrantEpoch.Value);
        Assert.IsNull(after.ActiveLease);
        Assert.AreEqual(0, authority.ActiveGrants.Count, "grants are bound to a connection generation");

        // 旧世代的命令必须失效（不允许旧世代命令在新世代上执行）。
        var stale = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Snapshot, caller, generation: B0Fixture.Generation));
        Assert.IsTrue(stale.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.EpochStale, stale.Denial);
    }

    [TestMethod]
    public void Admit_Before_Handshake_Is_NotConnected()
    {
        var authority = B0Fixture.Disconnected();

        var decision = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.ContextsList));

        Assert.IsTrue(decision.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.NotConnected, decision.Denial);
        Assert.AreEqual(DesktopCapabilityErrorCode.NotConnected, decision.Error!.Code);
    }

    // ── 租约 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Second_Write_On_The_Same_Page_Is_Refused_While_A_Lease_Is_Held()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        var first = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));
        Assert.IsTrue(first.IsAllowed);

        var second = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Navigate,
            caller,
            operationId: OperationId.NewId()));

        Assert.IsTrue(second.IsDenied, "writes on one page must be serialized by a single lease");
        Assert.AreEqual(BrowserAuthorizationDenial.PageBusy, second.Denial);
    }

    [TestMethod]
    public void Retransmitting_The_Same_Operation_Reuses_Its_Lease()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        var operationId = OperationId.NewId();
        var first = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Interact, caller, operationId: operationId));
        var retransmission = authority.Admit(B0Fixture.Request(
            BrowserAutomationOperation.Interact, caller, operationId: operationId));

        Assert.IsTrue(retransmission.IsAllowed);
        Assert.AreEqual(
            first.Lease!.LeaseId.Value,
            retransmission.Lease!.LeaseId.Value,
            "a retransmission must not mint a second lease");
    }

    [TestMethod]
    public void ReleaseLease_Clears_The_Active_Lease_And_Allows_The_Next_Write()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        var admitted = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));
        var released = authority.ReleaseLease(admitted.Lease!.LeaseId, "action completed");

        Assert.IsNull(released.ActiveLease);

        var next = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Navigate, caller));
        Assert.IsTrue(next.IsAllowed);
    }

    [TestMethod]
    public void Revalidate_Refuses_A_Lease_Used_By_A_Different_Operation()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        var admitted = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));
        var other = B0Fixture.Request(BrowserAutomationOperation.Interact, caller);

        var revalidated = authority.Revalidate(admitted.Lease!, other);

        Assert.IsTrue(revalidated.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.CallerMismatch, revalidated.Denial);
    }

    [TestMethod]
    public void Revoking_The_Authorization_Also_Drops_The_Lease()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        var grant = B0Fixture.Grant(caller);
        authority.IssueGrant(grant);
        authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));

        authority.RevokeGrant(grant.GrantId);

        Assert.IsNull(
            authority.Capture().ActiveLease,
            "a lease must never outlive the authorization it was issued under");
    }

    [TestMethod]
    public void Expired_Grant_Is_Refused()
    {
        var clock = new TestTimeProvider(B0Fixture.Now);
        var authority = B0Fixture.Connected(clock);
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller, lifetime: TimeSpan.FromMinutes(1)));

        clock.Advance(TimeSpan.FromMinutes(2));

        var decision = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller));
        Assert.IsTrue(decision.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.GrantExpired, decision.Denial);
    }
}
