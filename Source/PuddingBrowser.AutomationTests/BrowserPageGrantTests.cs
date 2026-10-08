using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// G2 页面授权门禁（设计方案 §3.2 / §11）：
/// <c>context trust</c> 不等于页面授权；非目标页在同 context 内同样被拒；
/// 撤销/切目标/frame 跨身份拒绝；子代理不得扩大父授权。
/// </summary>
[TestClass]
public sealed class BrowserPageGrantTests
{
    [TestMethod]
    public void Page_Without_Any_Grant_Is_Refused_For_Read_And_Write()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();

        var read = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller));
        var write = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller));

        Assert.IsTrue(read.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.NoGrant, read.Denial);
        Assert.IsTrue(write.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.NoGrant, write.Denial);
    }

    [TestMethod]
    public void Non_Target_Page_In_The_Same_Context_Is_Refused()
    {
        // 这是审计结论 #2 的直接门禁：Authored context 的 Trust 是 context 级，
        // 「Agent 目标」是 page 级 ⇒ 必须由 page grant 兜住，不能因为同 context 就放行。
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        var targetPage = B0Fixture.Page("page-target");
        var otherPage = B0Fixture.Page("page-other", targetPage.ContextId);

        authority.IssueGrant(B0Fixture.Grant(caller, target: targetPage));

        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller, targetPage)).IsAllowed,
            "the granted page must be readable");

        var other = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller, otherPage));
        Assert.IsTrue(other.IsDenied, "a sibling page in the same context must not inherit the grant");
        Assert.AreEqual(BrowserAuthorizationDenial.NoGrant, other.Denial);
    }

    [TestMethod]
    public void Read_Grant_Does_Not_Authorize_Writes()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller, BrowserPageGrantScope.Read));

        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller)).IsAllowed);

        var write = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Navigate, caller));
        Assert.IsTrue(write.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.ScopeInsufficient, write.Denial);
    }

    [TestMethod]
    public void Write_Grant_Does_Not_Authorize_Tab_Close_Without_Manage()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write));

        // 关闭已有页要 management 授权**加上**该页自己的 read 授权（方案 §3.2）。
        var close = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.TabClose, caller));
        Assert.IsTrue(close.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.ScopeInsufficient, close.Denial);

        authority.IssueGrant(B0Fixture.Grant(caller, BrowserPageGrantScope.Manage | BrowserPageGrantScope.Read));
        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.TabClose, caller)).IsAllowed);
    }

    [TestMethod]
    public void Revoked_Grant_Is_Refused_And_Advances_The_Grant_Epoch()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        var grant = B0Fixture.Grant(caller);
        authority.IssueGrant(grant);

        var epochBefore = authority.Capture().GrantEpoch;
        var epochAfter = authority.RevokeGrant(grant.GrantId);

        Assert.IsTrue(epochAfter.Value > epochBefore.Value, "revocation must advance the grant epoch");

        var decision = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller));
        Assert.IsTrue(decision.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.NoGrant, decision.Denial);
    }

    [TestMethod]
    public void RevokeExecutionScope_Keeps_Read_But_Drops_Write()
    {
        // 切换「Agent 目标」撤销旧页的执行权，但**不**撤销阅读授权（方案 §3.1/§3.2）。
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        var page = B0Fixture.Page();
        authority.IssueGrant(B0Fixture.Grant(caller, target: page));

        var epochBefore = authority.Capture().GrantEpoch;
        var epochAfter = authority.RevokeExecutionScope(page);

        Assert.IsTrue(epochAfter.Value > epochBefore.Value);

        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller, page)).IsAllowed,
            "read authorization must survive an execution-scope revocation");
        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Interact, caller, page)).IsDenied,
            "write authorization must be gone");
    }

    [TestMethod]
    public void RevokeAllGrants_Drops_Read_And_Write()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller));

        authority.RevokeAllGrants();

        Assert.AreEqual(0, authority.ActiveGrants.Count);
        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller)).IsDenied);
    }

    [TestMethod]
    public void Grant_Bound_To_Another_Desktop_Or_Process_Is_Refused()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();

        // 同世代但换了 Desktop 实例 / 进程实例：签发时看不出问题，**准入时必须被拒**。
        var otherDesktop = new BrowserPageGrantBinding(
            new DesktopInstanceId("desktop-other"),
            B0Fixture.ProcessId,
            B0Fixture.Generation);
        var otherProcess = new BrowserPageGrantBinding(
            B0Fixture.DesktopId,
            new DesktopProcessInstanceId("process-other"),
            B0Fixture.Generation);

        authority.IssueGrant(B0Fixture.Grant(caller, binding: otherDesktop));
        authority.IssueGrant(B0Fixture.Grant(caller, binding: otherProcess));

        var decision = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller));
        Assert.IsTrue(decision.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.BindingMismatch, decision.Denial);
    }

    [TestMethod]
    public void Grant_Bound_To_A_Frame_Does_Not_Authorize_The_Top_Document()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueGrant(B0Fixture.Grant(caller, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write, frameId: "frame-1"));

        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller, frameId: "frame-1")).IsAllowed,
            "the frame the grant was issued for stays readable");

        var top = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller));
        Assert.IsTrue(top.IsDenied, "a frame grant must not authorize the top document");
        Assert.AreEqual(BrowserAuthorizationDenial.FrameMismatch, top.Denial);

        var otherFrame = authority.Admit(
            B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller, frameId: "frame-2"));
        Assert.IsTrue(otherFrame.IsDenied, "a frame grant must not leak to another frame");
        Assert.AreEqual(BrowserAuthorizationDenial.FrameMismatch, otherFrame.Denial);
    }

    // ── 子代理 ────────────────────────────────────────────────────────────

    [TestMethod]
    public void SubAgent_Cannot_Borrow_Its_Parents_Grant()
    {
        var authority = B0Fixture.Connected();
        var parent = B0Fixture.Agent();
        var subAgent = B0Fixture.SubAgent(parentTaskId: parent.TaskId!);
        authority.IssueGrant(B0Fixture.Grant(parent));

        var decision = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, subAgent));

        Assert.IsTrue(decision.IsDenied, "a sub-agent must hold its own grant, not reuse the parent's");
        Assert.AreEqual(BrowserAuthorizationDenial.CallerMismatch, decision.Denial);
    }

    [TestMethod]
    public void SubAgent_Grant_Must_Be_A_Subset_Of_The_Parents_Scope()
    {
        var authority = B0Fixture.Connected();
        var parent = B0Fixture.Agent();
        var subAgent = B0Fixture.SubAgent(parentTaskId: parent.TaskId!);

        // 父任务的授权是子代理范围的**上界**。
        authority.IssueGrant(B0Fixture.Grant(parent, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write));

        // 子集：只读 —— 允许。
        authority.IssueGrant(B0Fixture.Grant(subAgent, BrowserPageGrantScope.Read));
        Assert.IsTrue(authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, subAgent)).IsAllowed);

        // 子代理有只读授权，但父任务只有只读时，子代理不能自己升到写。
        var readOnlyParentAuthority = B0Fixture.Connected();
        var readOnlyParent = B0Fixture.Agent();
        var child = B0Fixture.SubAgent(parentTaskId: readOnlyParent.TaskId!);
        readOnlyParentAuthority.IssueGrant(B0Fixture.Grant(readOnlyParent, BrowserPageGrantScope.Read));

        Assert.ThrowsExactly<ArgumentException>(
            () => readOnlyParentAuthority.IssueGrant(
                B0Fixture.Grant(child, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write)));
    }

    [TestMethod]
    public void SubAgent_Without_Any_Parent_Grant_Is_Refused_At_Issuance()
    {
        var authority = B0Fixture.Connected();
        var subAgent = B0Fixture.SubAgent(parentTaskId: "task-unknown");

        var grant = B0Fixture.Grant(subAgent, BrowserPageGrantScope.Read);

        Assert.ThrowsExactly<ArgumentException>(() => authority.IssueGrant(grant));
    }

    [TestMethod]
    public void SubAgent_Scope_Check_Is_Pure_And_Reports_The_Denial()
    {
        var parent = B0Fixture.Agent();
        var subAgent = B0Fixture.SubAgent(parentTaskId: parent.TaskId!);
        var parentGrant = B0Fixture.Grant(parent, BrowserPageGrantScope.Read);

        Assert.IsNull(
            BrowserGrantIssuancePolicy.CheckSubAgentScope(
                B0Fixture.Grant(subAgent, BrowserPageGrantScope.Read),
                [parentGrant]),
            "a read-only sub-agent grant is a subset of a read-only parent grant");

        Assert.AreEqual(
            BrowserAuthorizationDenial.ScopeInsufficient,
            BrowserGrantIssuancePolicy.CheckSubAgentScope(
                B0Fixture.Grant(subAgent, BrowserPageGrantScope.Read | BrowserPageGrantScope.Write),
                [parentGrant]));

        Assert.AreEqual(
            BrowserAuthorizationDenial.NoGrant,
            BrowserGrantIssuancePolicy.CheckSubAgentScope(
                B0Fixture.Grant(subAgent, BrowserPageGrantScope.Read),
                []));

        // 非子代理不做上界检查（Agent 的授权由 Runtime 签发）。
        Assert.IsNull(BrowserGrantIssuancePolicy.CheckSubAgentScope(B0Fixture.Grant(parent), []));
    }

    // ── 任务级管理授权 ────────────────────────────────────────────────────

    [TestMethod]
    public void Management_Operations_Require_A_Task_Level_Grant()
    {
        foreach (var operation in new[]
                 {
                     BrowserAutomationOperation.ContextCreate,
                     BrowserAutomationOperation.ContextClose,
                     BrowserAutomationOperation.TabNew,
                 })
        {
            var caller = B0Fixture.Agent();

            var withoutGrant = B0Fixture.Connected();
            var denied = withoutGrant.Admit(B0Fixture.Request(operation, caller));
            Assert.IsTrue(denied.IsDenied, $"{operation} must require a management grant");
            Assert.AreEqual(BrowserAuthorizationDenial.NoManagementGrant, denied.Denial);

            var withGrant = B0Fixture.Connected();
            withGrant.IssueManagementGrant(B0Fixture.ManagementGrant(caller));
            Assert.IsTrue(
                withGrant.Admit(B0Fixture.Request(operation, caller)).IsAllowed,
                $"{operation} must be allowed once the task holds a management grant");
        }
    }

    [TestMethod]
    public void Management_Is_Refused_During_Takeover()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        authority.IssueManagementGrant(B0Fixture.ManagementGrant(caller));
        authority.SetUserTakeover(true);

        var decision = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.TabNew, caller));

        Assert.IsTrue(decision.IsDenied, "creating a tab is a side effect and must be refused during takeover");
        Assert.AreEqual(BrowserAuthorizationDenial.UserTakeover, decision.Denial);
    }

    [TestMethod]
    public void Management_Grant_Is_Shared_Inside_The_Task_Scope()
    {
        var authority = B0Fixture.Connected();
        var parent = B0Fixture.Agent();
        var subAgent = B0Fixture.SubAgent(parentTaskId: parent.TaskId!);
        authority.IssueManagementGrant(B0Fixture.ManagementGrant(parent));

        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.TabNew, subAgent)).IsAllowed,
            "management is a task-level flag; a sub-agent in the same task scope inherits it");
    }

    [TestMethod]
    public void Revoked_Management_Grant_Is_Refused()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();
        var grant = B0Fixture.ManagementGrant(caller);
        authority.IssueManagementGrant(grant);

        authority.RevokeManagementGrant(grant.GrantId);

        var decision = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.ContextCreate, caller));
        Assert.IsTrue(decision.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.NoManagementGrant, decision.Denial);
    }

    // ── 调用者身份 ────────────────────────────────────────────────────────

    [TestMethod]
    public void Untrusted_Caller_Is_Refused_Even_With_A_Grant()
    {
        var authority = B0Fixture.Connected();
        var user = B0Fixture.User();
        authority.IssueGrant(B0Fixture.Grant(user));

        var decision = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.Snapshot, user));

        Assert.IsTrue(decision.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.UntrustedCaller, decision.Denial);
    }

    [TestMethod]
    public void Contexts_List_Needs_Trusted_Identity_But_No_Page_Grant()
    {
        var authority = B0Fixture.Connected();

        Assert.IsTrue(
            authority.Admit(B0Fixture.Request(BrowserAutomationOperation.ContextsList)).IsAllowed,
            "browser-scoped metadata needs no page grant");

        var untrusted = authority.Admit(B0Fixture.Request(BrowserAutomationOperation.ContextsList, B0Fixture.User()));
        Assert.IsTrue(untrusted.IsDenied);
        Assert.AreEqual(BrowserAuthorizationDenial.UntrustedCaller, untrusted.Denial);
    }

    [TestMethod]
    public void Grant_Before_Handshake_Is_Refused()
    {
        var authority = B0Fixture.Disconnected();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => authority.IssueGrant(B0Fixture.Grant(B0Fixture.Agent())));
    }

    [TestMethod]
    public void Grant_Bound_To_A_Stale_Generation_Is_Refused()
    {
        var authority = B0Fixture.Connected();

        Assert.ThrowsExactly<ArgumentException>(
            () => authority.IssueGrant(B0Fixture.Grant(
                B0Fixture.Agent(),
                binding: B0Fixture.Binding(ConnectionGeneration.Require(99)))));
    }

    [TestMethod]
    public void Denied_Request_Text_Carries_No_Grant_Or_Page_Content()
    {
        var authority = B0Fixture.Connected();
        var caller = B0Fixture.Agent();

        var decision = authority.Admit(
            B0Fixture.Request(BrowserAutomationOperation.Snapshot, caller, B0Fixture.Page("page-42")));

        Assert.IsTrue(decision.IsDenied);
        StringAssert.Contains(decision.Error!.Message, "ctx-1/page-42");
        Assert.IsFalse(decision.Error.Message.Contains("Pudding", StringComparison.Ordinal));
    }
}
