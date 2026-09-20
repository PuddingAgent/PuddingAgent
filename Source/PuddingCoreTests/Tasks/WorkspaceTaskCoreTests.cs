using PuddingCode.Tasks;

namespace PuddingCoreTests.Tasks;

[TestClass]
public sealed class WorkspaceTaskCoreTests
{
    // ── 1. BoardColumn 投影 ────────────────────────────────────────────────

    [TestMethod]
    public void ProjectBoardColumn_Maps_All_Board_States_To_Five_Columns()
    {
        Assert.AreEqual(BoardColumn.Backlog, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Backlog));

        Assert.AreEqual(BoardColumn.Todo, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Ready));
        Assert.AreEqual(BoardColumn.Todo, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Deferred));
        Assert.AreEqual(BoardColumn.Todo, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Reserved));
        Assert.AreEqual(BoardColumn.Todo, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Assigned));
        Assert.AreEqual(BoardColumn.Todo, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.NeedsReview));

        Assert.AreEqual(BoardColumn.InProgress, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.InProgress));
        Assert.AreEqual(BoardColumn.InProgress, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Blocked));

        Assert.AreEqual(BoardColumn.Done, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Completed));
        Assert.AreEqual(BoardColumn.Failed, TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Failed));
    }

    [TestMethod]
    public void ProjectBoardColumn_Cancelled_And_Archived_Do_Not_Occupy_Columns()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Cancelled));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => TaskStateMachine.ProjectBoardColumn(WorkspaceTaskStatus.Archived));
    }

    // ── 2. 状态机转换表 ────────────────────────────────────────────────────

    [TestMethod]
    public void CanTransition_Allows_All_Legal_Transitions()
    {
        var legal = new (WorkspaceTaskStatus From, WorkspaceTaskStatus To)[]
        {
            (WorkspaceTaskStatus.Backlog, WorkspaceTaskStatus.Ready),
            // 278042d（fix(board): 卡状态机补终态关闭通道，看板卡 2a92b3ed）：Backlog/Deferred/
            // Reserved/NeedsReview 各补 Cancelled 终态出边（TaskStateMachine.cs BuildTransitions）。
            (WorkspaceTaskStatus.Backlog, WorkspaceTaskStatus.Cancelled),

            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Deferred),
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Reserved),
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.NeedsReview),
            // 40c3065（fix(board): 交付发生在 claim 通道之外时也能关闭卡片）：
            // Ready/NeedsReview 补 Completed「验收即关闭」出边（卡 cce95d6b）。
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Completed),
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Cancelled),

            (WorkspaceTaskStatus.Deferred, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.Deferred, WorkspaceTaskStatus.Cancelled), // 278042d

            (WorkspaceTaskStatus.Reserved, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.Reserved, WorkspaceTaskStatus.Assigned),
            (WorkspaceTaskStatus.Reserved, WorkspaceTaskStatus.Cancelled), // 278042d

            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.InProgress),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Blocked),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Completed),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Failed),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.NeedsReview),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Cancelled),

            (WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.Completed), // 40c3065
            (WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.Cancelled), // 278042d

            (WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.Blocked),
            (WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.Failed),
            (WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.Completed),
            (WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.NeedsReview),
            (WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.Cancelled),

            (WorkspaceTaskStatus.Blocked, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.Blocked, WorkspaceTaskStatus.Failed),
            (WorkspaceTaskStatus.Blocked, WorkspaceTaskStatus.Cancelled),

            (WorkspaceTaskStatus.Completed, WorkspaceTaskStatus.Archived),
            (WorkspaceTaskStatus.Failed, WorkspaceTaskStatus.Archived),
            (WorkspaceTaskStatus.Cancelled, WorkspaceTaskStatus.Archived)
        };

        foreach (var (from, to) in legal)
        {
            Assert.IsTrue(
                TaskStateMachine.CanTransition(from, to),
                $"Expected CanTransition({from}, {to}) == true");
        }
    }

    [TestMethod]
    public void CanTransition_Rejects_Sampled_Illegal_Transitions()
    {
        var illegal = new (WorkspaceTaskStatus From, WorkspaceTaskStatus To)[]
        {
            (WorkspaceTaskStatus.Backlog, WorkspaceTaskStatus.Assigned),
            (WorkspaceTaskStatus.Backlog, WorkspaceTaskStatus.Failed),
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.InProgress),
            // 40c3065 后 Ready→Completed 已是合法边（移入 Allows 测试）；改用 Ready→Assigned
            // 守卫「claim/领取通道不可绕过」的未放宽承诺（40c3065 提交说明）。
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Assigned),
            (WorkspaceTaskStatus.Deferred, WorkspaceTaskStatus.Assigned),
            (WorkspaceTaskStatus.Reserved, WorkspaceTaskStatus.InProgress),
            (WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.InProgress),
            (WorkspaceTaskStatus.Completed, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.Failed, WorkspaceTaskStatus.Ready), // Reopen 特例，普通转换非法
            (WorkspaceTaskStatus.Cancelled, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.Archived, WorkspaceTaskStatus.Ready)
        };

        foreach (var (from, to) in illegal)
        {
            Assert.IsFalse(
                TaskStateMachine.CanTransition(from, to),
                $"Expected CanTransition({from}, {to}) == false");
        }
    }

    [TestMethod]
    public void GetAllowedTransitions_Returns_Expected_Sets()
    {
        // 按现行 BuildTransitions 全表逐态钉死（防再漂移）。
        // 契约变更证据：278042d 给 Backlog/Deferred/Reserved/NeedsReview 补 Cancelled 出边；
        // 40c3065 给 Ready/NeedsReview 补 Completed「验收即关闭」出边。
        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Backlog),
            WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Cancelled);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Ready),
            WorkspaceTaskStatus.Deferred, WorkspaceTaskStatus.Reserved,
            WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.Completed,
            WorkspaceTaskStatus.Cancelled);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Deferred),
            WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Cancelled);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Reserved),
            WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Assigned,
            WorkspaceTaskStatus.Cancelled);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Assigned),
            WorkspaceTaskStatus.InProgress, WorkspaceTaskStatus.Blocked,
            WorkspaceTaskStatus.Completed, WorkspaceTaskStatus.Failed,
            WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.NeedsReview,
            WorkspaceTaskStatus.Cancelled);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.NeedsReview),
            WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Completed,
            WorkspaceTaskStatus.Cancelled);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.InProgress),
            WorkspaceTaskStatus.Blocked, WorkspaceTaskStatus.Ready,
            WorkspaceTaskStatus.Failed, WorkspaceTaskStatus.Completed,
            WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.Cancelled);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Blocked),
            WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Failed,
            WorkspaceTaskStatus.Cancelled);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Completed),
            WorkspaceTaskStatus.Archived);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Failed),
            WorkspaceTaskStatus.Archived);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Cancelled),
            WorkspaceTaskStatus.Archived);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Archived));
    }

    // ── 3. Reopen 特例 ─────────────────────────────────────────────────────

    [TestMethod]
    public void Reopen_Is_Special_Case_Not_Ordinary_Transition()
    {
        Assert.IsFalse(TaskStateMachine.CanTransition(WorkspaceTaskStatus.Failed, WorkspaceTaskStatus.Ready));

        var ok = TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Failed, TaskCommand.Reopen, out var next);

        Assert.IsTrue(ok);
        Assert.AreEqual(WorkspaceTaskStatus.Ready, next);
    }

    // ── 4. Command 映射 ────────────────────────────────────────────────────

    [TestMethod]
    public void TryApplyCommand_Create_Always_Produces_Backlog()
    {
        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Ready, TaskCommand.Create, out var next));
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, next);
    }

    [TestMethod]
    public void TryApplyCommand_Update_Keeps_NonTerminal_State()
    {
        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.InProgress, TaskCommand.Update, out var next));
        Assert.AreEqual(WorkspaceTaskStatus.InProgress, next);

        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Completed, TaskCommand.Update, out _));
        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Archived, TaskCommand.Update, out _));
    }

    [TestMethod]
    public void TryApplyCommand_Assign_And_RunNow_Produce_Reserved()
    {
        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Ready, TaskCommand.Assign, out var next));
        Assert.AreEqual(WorkspaceTaskStatus.Reserved, next);

        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Deferred, TaskCommand.Assign, out _));

        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Ready, TaskCommand.RunNow, out next));
        Assert.AreEqual(WorkspaceTaskStatus.Reserved, next);

        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Deferred, TaskCommand.RunNow, out next));
        Assert.AreEqual(WorkspaceTaskStatus.Reserved, next);

        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Assigned, TaskCommand.RunNow, out _));
    }

    [TestMethod]
    public void TryApplyCommand_Cancel_Allows_All_NonTerminal_Statuses()
    {
        // 278042d：Cancel 合法来源由 {Ready, Assigned, InProgress, Blocked} 扩为全部 8 个非终态
        // （+Backlog/Deferred/Reserved/NeedsReview），与 BuildTransitions 的 Cancelled 出边同源。
        var allowed = new[]
        {
            WorkspaceTaskStatus.Backlog,
            WorkspaceTaskStatus.Ready,
            WorkspaceTaskStatus.Deferred,
            WorkspaceTaskStatus.Reserved,
            WorkspaceTaskStatus.Assigned,
            WorkspaceTaskStatus.InProgress,
            WorkspaceTaskStatus.Blocked,
            WorkspaceTaskStatus.NeedsReview
        };

        foreach (var from in allowed)
        {
            Assert.IsTrue(TaskStateMachine.TryApplyCommand(from, TaskCommand.Cancel, out var next));
            Assert.AreEqual(WorkspaceTaskStatus.Cancelled, next);
        }

        // 终态一律不可再取消。
        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Completed, TaskCommand.Cancel, out _));
        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Failed, TaskCommand.Cancel, out _));
        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Cancelled, TaskCommand.Cancel, out _));
        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Archived, TaskCommand.Cancel, out _));
    }

    [TestMethod]
    public void TryApplyCommand_Archive_Allows_Completed_Cancelled_Failed()
    {
        var allowed = new[]
        {
            WorkspaceTaskStatus.Completed,
            WorkspaceTaskStatus.Cancelled,
            WorkspaceTaskStatus.Failed
        };

        foreach (var from in allowed)
        {
            Assert.IsTrue(TaskStateMachine.TryApplyCommand(from, TaskCommand.Archive, out var next));
            Assert.AreEqual(WorkspaceTaskStatus.Archived, next);
        }

        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Ready, TaskCommand.Archive, out _));
    }

    [TestMethod]
    public void TryApplyCommand_MarkFailed_Allows_Assigned_InProgress_Blocked()
    {
        var allowed = new[]
        {
            WorkspaceTaskStatus.Assigned,
            WorkspaceTaskStatus.InProgress,
            WorkspaceTaskStatus.Blocked
        };

        foreach (var from in allowed)
        {
            Assert.IsTrue(TaskStateMachine.TryApplyCommand(from, TaskCommand.MarkFailed, out var next));
            Assert.AreEqual(WorkspaceTaskStatus.Failed, next);
        }

        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Ready, TaskCommand.MarkFailed, out _));
    }

    [TestMethod]
    public void TryApplyCommand_Resume_And_Requeue()
    {
        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Blocked, TaskCommand.Resume, out var next));
        Assert.AreEqual(WorkspaceTaskStatus.Ready, next);

        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.NeedsReview, TaskCommand.Resume, out next));
        Assert.AreEqual(WorkspaceTaskStatus.Ready, next);

        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Ready, TaskCommand.Resume, out _));

        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Deferred, TaskCommand.Requeue, out next));
        Assert.AreEqual(WorkspaceTaskStatus.Ready, next);

        Assert.IsTrue(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Ready, TaskCommand.Requeue, out next));
        Assert.AreEqual(WorkspaceTaskStatus.Ready, next);

        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Assigned, TaskCommand.Requeue, out _));
    }

    // ── 5. disposition 映射 ────────────────────────────────────────────────

    [TestMethod]
    public void TryInterpretDisposition_Accept_And_Progress()
    {
        Assert.IsTrue(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.Assigned, TaskDisposition.Accept, out var next));
        Assert.AreEqual(WorkspaceTaskStatus.InProgress, next);

        Assert.IsFalse(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.InProgress, TaskDisposition.Accept, out _));

        Assert.IsTrue(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.InProgress, TaskDisposition.Progress, out next));
        Assert.AreEqual(WorkspaceTaskStatus.InProgress, next);

        Assert.IsFalse(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.Ready, TaskDisposition.Progress, out _));
    }

    [TestMethod]
    public void TryInterpretDisposition_Todo_Returns_To_Ready()
    {
        var allowed = new[]
        {
            WorkspaceTaskStatus.InProgress,
            WorkspaceTaskStatus.Blocked,
            WorkspaceTaskStatus.NeedsReview
        };

        foreach (var from in allowed)
        {
            Assert.IsTrue(TaskStateMachine.TryInterpretDisposition(from, TaskDisposition.Todo, out var next));
            Assert.AreEqual(WorkspaceTaskStatus.Ready, next);
        }

        Assert.IsFalse(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.Ready, TaskDisposition.Todo, out _));
    }

    [TestMethod]
    public void TryInterpretDisposition_Blocked_And_NeedsApproval()
    {
        foreach (var from in new[] { WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.InProgress })
        {
            Assert.IsTrue(TaskStateMachine.TryInterpretDisposition(from, TaskDisposition.Blocked, out var next));
            Assert.AreEqual(WorkspaceTaskStatus.Blocked, next);

            Assert.IsTrue(TaskStateMachine.TryInterpretDisposition(from, TaskDisposition.NeedsApproval, out next));
            Assert.AreEqual(WorkspaceTaskStatus.Blocked, next);
        }

        Assert.IsFalse(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.Ready, TaskDisposition.Blocked, out _));

        Assert.IsFalse(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.Blocked, TaskDisposition.NeedsApproval, out _));
    }

    [TestMethod]
    public void TryInterpretDisposition_Rejected_And_Completed()
    {
        Assert.IsTrue(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.Assigned, TaskDisposition.Rejected, out var next));
        Assert.AreEqual(WorkspaceTaskStatus.Ready, next);

        Assert.IsFalse(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.InProgress, TaskDisposition.Rejected, out _));

        Assert.IsTrue(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.InProgress, TaskDisposition.Completed, out next));
        Assert.AreEqual(WorkspaceTaskStatus.Completed, next);

        Assert.IsFalse(TaskStateMachine.TryInterpretDisposition(
            WorkspaceTaskStatus.Assigned, TaskDisposition.Completed, out _));
    }

    // ── 6. 终态判断 ────────────────────────────────────────────────────────

    [TestMethod]
    public void IsTerminal_Returns_True_Only_For_Completed_Failed_Cancelled_Archived()
    {
        var terminal = new[]
        {
            WorkspaceTaskStatus.Completed,
            WorkspaceTaskStatus.Failed,
            WorkspaceTaskStatus.Cancelled,
            WorkspaceTaskStatus.Archived
        };

        foreach (var status in Enum.GetValues<WorkspaceTaskStatus>())
        {
            Assert.AreEqual(
                terminal.Contains(status),
                TaskStateMachine.IsTerminal(status),
                $"IsTerminal({status}) mismatch");
        }
    }

    [TestMethod]
    public void IsClosed_Returns_True_Only_For_Completed_Failed()
    {
        Assert.IsTrue(TaskStateMachine.IsClosed(WorkspaceTaskStatus.Completed));
        Assert.IsTrue(TaskStateMachine.IsClosed(WorkspaceTaskStatus.Failed));

        Assert.IsFalse(TaskStateMachine.IsClosed(WorkspaceTaskStatus.Cancelled));
        Assert.IsFalse(TaskStateMachine.IsClosed(WorkspaceTaskStatus.Archived));
        Assert.IsFalse(TaskStateMachine.IsClosed(WorkspaceTaskStatus.Ready));
        Assert.IsFalse(TaskStateMachine.IsClosed(WorkspaceTaskStatus.InProgress));
    }

    // ── 7. 枚举完整性 ──────────────────────────────────────────────────────

    [TestMethod]
    public void Enums_Contain_Exactly_Contract_Frozen_Member_Counts()
    {
        Assert.AreEqual(12, Enum.GetValues<WorkspaceTaskStatus>().Length);
        Assert.AreEqual(5, Enum.GetValues<BoardColumn>().Length);
        Assert.AreEqual(7, Enum.GetValues<TaskDisposition>().Length);
        // Stage 2 顺带修复（pre-existing 缺陷，与本特性无关）：TaskOrigin 有 4 个成员
        // （Manual / Auto / AutomationSchedule / ExternalApi），ExternalApi 是早前新增的成员，
        // 但本冻结断言当时漏更新，导致本测试类在 HEAD 上就有 1 条失败。
        Assert.AreEqual(4, Enum.GetValues<TaskOrigin>().Length);
        Assert.AreEqual(4, Enum.GetValues<TaskPriority>().Length);
        Assert.AreEqual(3, Enum.GetValues<TaskExecutionWindow>().Length);
        Assert.AreEqual(15, Enum.GetValues<DecisionCode>().Length);
        Assert.AreEqual(10, Enum.GetValues<TaskCommand>().Length);
        // Stage 1（D1/D4）新增 3 个父层级错误码：TaskParentNotFound / TaskHierarchyInvalid /
        // TaskHasNonTerminalChildren；既有 20 个成员未删除、未改名、未改序，总数 20 → 23。
        // 3df8c7c（feat(tasks): expose board-card dependencies in manage_tasks）在 enum TaskErrorCode
        // 末尾纯追加 TaskDependencyTaskNotFound / TaskDependencyInvalid，总数 23 → 25。
        // 下方同时钉住成员名集合，防成员增删/改名再漂移。
        Assert.AreEqual(25, Enum.GetValues<TaskErrorCode>().Length);
        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(TaskErrorCode.TaskNotFound),
                nameof(TaskErrorCode.TaskVersionConflict),
                nameof(TaskErrorCode.TaskStateConflict),
                nameof(TaskErrorCode.TaskInvalidTransition),
                nameof(TaskErrorCode.TaskInvalidDisposition),
                nameof(TaskErrorCode.TaskReasonRequired),
                nameof(TaskErrorCode.TaskResultRequired),
                nameof(TaskErrorCode.TaskArtifactRequired),
                nameof(TaskErrorCode.TaskNotReopenable),
                nameof(TaskErrorCode.TaskCannotHardDelete),
                nameof(TaskErrorCode.AssignmentNotFound),
                nameof(TaskErrorCode.AssignmentAlreadyActive),
                nameof(TaskErrorCode.AssignmentStale),
                nameof(TaskErrorCode.AgentNotFound),
                nameof(TaskErrorCode.AgentUnavailable),
                nameof(TaskErrorCode.CapabilityMissing),
                nameof(TaskErrorCode.PolicyInvalid),
                nameof(TaskErrorCode.PolicyVersionConflict),
                nameof(TaskErrorCode.TaskActiveContextMissing),
                nameof(TaskErrorCode.TaskInvalidCursor),
                nameof(TaskErrorCode.TaskParentNotFound),
                nameof(TaskErrorCode.TaskHierarchyInvalid),
                nameof(TaskErrorCode.TaskHasNonTerminalChildren),
                nameof(TaskErrorCode.TaskDependencyTaskNotFound),
                nameof(TaskErrorCode.TaskDependencyInvalid)
            },
            Enum.GetNames<TaskErrorCode>());
        // Stage 3（本轮收口）：TaskEventType 的既有漂移——TaskEvaluated（ADR-075 评价追加）
        // 是早前新增的成员，但本冻结断言当时漏更新（与上方 TaskOrigin 同一类漂移），
        // 导致本测试类在 HEAD 上有 1 条失败；本次按现网契约同步 17 → 18（不改枚举本身）。
        Assert.AreEqual(18, Enum.GetValues<TaskEventType>().Length);
        Assert.AreEqual(4, Enum.GetValues<AssignmentStatus>().Length);
    }

    // ── 8. DTO 默认值 ──────────────────────────────────────────────────────

    [TestMethod]
    public void Request_And_Query_Defaults_Are_Contract_Conformant()
    {
        var create = new CreateTaskRequest { WorkspaceId = "ws", Title = "t" };
        Assert.AreEqual(TaskPriority.P3, create.Priority);
        Assert.AreEqual(TaskExecutionWindow.Inherit, create.ExecutionWindow);

        var update = new UpdateTaskRequest { TaskId = "t1", ExpectedVersion = 1 };
        Assert.IsNull(update.Title);
        Assert.IsNull(update.Priority);

        var query = new TaskQuery { WorkspaceId = "ws" };
        Assert.AreEqual(100, query.Limit);
        Assert.IsNull(query.Status);
        Assert.IsNull(query.Cursor);
    }

    [TestMethod]
    public void WorkspaceTask_Defaults_Are_Contract_Conformant()
    {
        var task = new WorkspaceTask { TaskId = "t1", WorkspaceId = "ws", Title = "title" };

        Assert.AreEqual(WorkspaceTaskStatus.Backlog, task.Status);
        Assert.AreEqual(TaskPriority.P3, task.Priority);
        Assert.AreEqual(TaskExecutionWindow.Inherit, task.ExecutionWindow);
        Assert.AreEqual(1, task.Version);
        Assert.AreNotEqual(default, task.CreatedAtUtc);
        Assert.AreNotEqual(default, task.UpdatedAtUtc);
        Assert.IsNull(task.CompletedAtUtc);
        // Stage 1（D1）：新字段默认无父（既有数据与新建任务一律为 null，不回填）。
        Assert.IsNull(task.ParentTaskId);
    }

    [TestMethod]
    public void WorkspaceTask_ParentTaskId_DefaultsToNull_AndIsSingleLevelPointer()
    {
        var parent = new WorkspaceTask { TaskId = "p", WorkspaceId = "ws", Title = "p" };
        var child = new WorkspaceTask { TaskId = "c", WorkspaceId = "ws", Title = "c", ParentTaskId = parent.TaskId };

        Assert.IsNull(parent.ParentTaskId);
        Assert.AreEqual("p", child.ParentTaskId);
        // D1：单层——父自身无父；容器判定与派发禁令为纯函数，不改写 status（D2/D3）。
        Assert.IsNull(TaskHierarchyRules.ValidateParentAssignment(child.TaskId, parent.TaskId, [parent, child]));
        Assert.IsTrue(TaskHierarchyRules.IsContainer(parent.TaskId, [parent, child]));
        Assert.IsFalse(TaskHierarchyRules.CanBeDispatched(parent.TaskId, [parent, child]));
        Assert.IsFalse(TaskHierarchyRules.IsAutoDispatchEffective(parent.TaskId, true, [parent, child]));
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, parent.Status);
    }

    private static void AssertSet(IReadOnlySet<WorkspaceTaskStatus> actual, params WorkspaceTaskStatus[] expected)
    {
        Assert.AreEqual(expected.Length, actual.Count, "目标状态集合大小不匹配。");
        foreach (var item in expected)
        {
            Assert.IsTrue(actual.Contains(item), $"缺少目标状态 {item}。");
        }
    }
}
