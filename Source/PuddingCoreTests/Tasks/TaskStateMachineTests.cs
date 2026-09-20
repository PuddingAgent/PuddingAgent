using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tasks;

namespace PuddingCoreTests.Tasks;

/// <summary>
/// TaskStateMachine 纯函数回归。
///
/// 背景（卡 cce95d6b，2026-09-20）：交付可能发生在 claim 通道之外 —— 当 Agent 没有
/// 运行时注入的 Active Task Context 时无法 claim/complete，已交付的卡只能停在 Ready，
/// 而 Ready 此前没有任何终态出边（现场实测 <c>task.invalid_transition</c>），
/// 交付事实无法入账。这里补的是「验收即关闭」两条出边，同时确认没有顺带打开
/// claim 旁路（Ready 仍不得直连 Assigned/InProgress）。
/// </summary>
[TestClass]
public sealed class TaskStateMachineTests
{
    [TestMethod]
    public void ReadyAndNeedsReview_CanTransitionToCompleted()
    {
        Assert.IsTrue(
            TaskStateMachine.CanTransition(WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Completed),
            "Ready 必须能关闭：交付可能发生在 claim 通道之外。");
        Assert.IsTrue(
            TaskStateMachine.CanTransition(WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.Completed),
            "NeedsReview 必须能关闭：验收通过即终态。");
    }

    [TestMethod]
    public void Ready_CannotSkipClaimPath()
    {
        Assert.IsFalse(
            TaskStateMachine.CanTransition(WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.InProgress),
            "Ready 不得直连 InProgress：派发/领取通道不能被绕过。");
        Assert.IsFalse(
            TaskStateMachine.CanTransition(WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Assigned),
            "Ready 不得直连 Assigned：领取通道不能被绕过。");
    }

    [TestMethod]
    public void TerminalStates_RemainClosed()
    {
        Assert.IsFalse(TaskStateMachine.CanTransition(WorkspaceTaskStatus.Completed, WorkspaceTaskStatus.Ready));
        Assert.IsFalse(TaskStateMachine.CanTransition(WorkspaceTaskStatus.Cancelled, WorkspaceTaskStatus.Ready));
        Assert.IsFalse(TaskStateMachine.CanTransition(WorkspaceTaskStatus.Archived, WorkspaceTaskStatus.Ready));
    }

    [TestMethod]
    public void Backlog_OutEdgesUnchanged()
    {
        var allowed = TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Backlog).ToArray();

        CollectionAssert.AreEquivalent(
            new[] { WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Cancelled },
            allowed,
            "Backlog 出边应保持 278042d 之后的状态（Ready + 关闭通道），不得再扩。");
    }
}
