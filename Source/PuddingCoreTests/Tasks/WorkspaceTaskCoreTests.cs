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

            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Deferred),
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Reserved),
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.NeedsReview),
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Cancelled),

            (WorkspaceTaskStatus.Deferred, WorkspaceTaskStatus.Ready),

            (WorkspaceTaskStatus.Reserved, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.Reserved, WorkspaceTaskStatus.Assigned),

            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.InProgress),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Blocked),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Completed),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Failed),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Ready),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.NeedsReview),
            (WorkspaceTaskStatus.Assigned, WorkspaceTaskStatus.Cancelled),

            (WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.Ready),

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
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.InProgress),
            (WorkspaceTaskStatus.Ready, WorkspaceTaskStatus.Completed),
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
        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Backlog),
            WorkspaceTaskStatus.Ready);

        AssertSet(TaskStateMachine.GetAllowedTransitions(WorkspaceTaskStatus.Ready),
            WorkspaceTaskStatus.Deferred, WorkspaceTaskStatus.Reserved,
            WorkspaceTaskStatus.NeedsReview, WorkspaceTaskStatus.Cancelled);

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
    public void TryApplyCommand_Cancel_Allows_Ready_Assigned_InProgress_Blocked()
    {
        var allowed = new[]
        {
            WorkspaceTaskStatus.Ready,
            WorkspaceTaskStatus.Assigned,
            WorkspaceTaskStatus.InProgress,
            WorkspaceTaskStatus.Blocked
        };

        foreach (var from in allowed)
        {
            Assert.IsTrue(TaskStateMachine.TryApplyCommand(from, TaskCommand.Cancel, out var next));
            Assert.AreEqual(WorkspaceTaskStatus.Cancelled, next);
        }

        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Completed, TaskCommand.Cancel, out _));
        Assert.IsFalse(TaskStateMachine.TryApplyCommand(
            WorkspaceTaskStatus.Backlog, TaskCommand.Cancel, out _));
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
        Assert.AreEqual(3, Enum.GetValues<TaskOrigin>().Length);
        Assert.AreEqual(4, Enum.GetValues<TaskPriority>().Length);
        Assert.AreEqual(3, Enum.GetValues<TaskExecutionWindow>().Length);
        Assert.AreEqual(15, Enum.GetValues<DecisionCode>().Length);
        Assert.AreEqual(10, Enum.GetValues<TaskCommand>().Length);
        Assert.AreEqual(20, Enum.GetValues<TaskErrorCode>().Length);
        Assert.AreEqual(17, Enum.GetValues<TaskEventType>().Length);
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
