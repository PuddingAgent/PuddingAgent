using PuddingCode.Tasks;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// Stage 1（D1–D5）：<see cref="TaskHierarchyRules"/> 纯逻辑单测 —— 容器判定、挂父校验各分支、
/// 归档拦截判定、禁止派发判定。全部为内存集合，无 IO、无数据库。
/// </summary>
[TestClass]
public sealed class TaskHierarchyRulesTests
{
    private static WorkspaceTask NewTask(
        string taskId,
        string? parentTaskId = null,
        WorkspaceTaskStatus status = WorkspaceTaskStatus.Backlog,
        string workspaceId = "ws-1")
        => new()
        {
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Title = taskId,
            ParentTaskId = parentTaskId,
            Status = status,
        };

    // ── 容器判定（D2）─────────────────────────────────────────────

    [TestMethod]
    public void IsContainer_TrueOnlyWhenChildExists()
    {
        var parent = NewTask("parent");
        var child = NewTask("child", parentTaskId: "parent");
        var unrelated = NewTask("other");

        Assert.IsTrue(TaskHierarchyRules.IsContainer("parent", [parent, child, unrelated]));
        Assert.IsFalse(TaskHierarchyRules.IsContainer("other", [parent, child, unrelated]));
        Assert.IsFalse(TaskHierarchyRules.IsContainer("parent", []));
        Assert.IsFalse(TaskHierarchyRules.IsContainer("parent", null));
        Assert.IsFalse(TaskHierarchyRules.IsContainer(null, [parent, child]));
        Assert.IsFalse(TaskHierarchyRules.IsContainer(string.Empty, [parent, child]));
    }

    [TestMethod]
    public void GetChildren_ReturnsDirectChildrenOnly()
    {
        var parent = NewTask("parent");
        var childA = NewTask("child-a", parentTaskId: "parent");
        var childB = NewTask("child-b", parentTaskId: "parent", status: WorkspaceTaskStatus.Completed);
        var grandChild = NewTask("child-a-1", parentTaskId: "child-a");
        var unrelated = NewTask("unrelated");

        var children = TaskHierarchyRules.GetChildren("parent", [grandChild, childB, unrelated, parent, childA]);

        Assert.AreEqual(2, children.Count);
        CollectionAssert.AreEquivalent(
            new[] { "child-a", "child-b" },
            children.Select(c => c.TaskId).ToArray());
        Assert.IsTrue(TaskHierarchyRules.HasChildren("child-a", [grandChild, childA]));
    }

    // ── 禁止派发判定（D2）─────────────────────────────────────────

    [TestMethod]
    public void CanBeDispatched_BlocksContainersOnly()
    {
        var container = NewTask("container");
        var child = NewTask("child", parentTaskId: "container");
        var leaf = NewTask("leaf");

        Assert.IsFalse(TaskHierarchyRules.CanBeDispatched("container", [container, child]));
        Assert.IsTrue(TaskHierarchyRules.CanBeDispatched("leaf", [container, child, leaf]));
        Assert.IsTrue(TaskHierarchyRules.CanBeDispatched("no-such-task", [container, child]));
    }

    [TestMethod]
    public void IsAutoDispatchEffective_ContainerAlwaysFalse()
    {
        var container = NewTask("container");
        var child = NewTask("child", parentTaskId: "container");
        var leaf = NewTask("leaf");
        WorkspaceTask[] tasks = [container, child, leaf];

        // 母卡即便显式打开 auto_dispatch_enabled，一旦成为容器也视为 false。
        Assert.IsTrue(container.AutoDispatchEnabled is false);
        Assert.IsFalse(TaskHierarchyRules.IsAutoDispatchEffective("container", autoDispatchEnabled: true, tasks));
        Assert.IsTrue(TaskHierarchyRules.IsAutoDispatchEffective("leaf", autoDispatchEnabled: true, tasks));
        Assert.IsFalse(TaskHierarchyRules.IsAutoDispatchEffective("leaf", autoDispatchEnabled: false, tasks));
    }

    // ── 挂父校验（D1 单层）────────────────────────────────────────

    [TestMethod]
    public void ValidateParentAssignment_ValidSingleLevelAndDetach()
    {
        var parent = NewTask("parent");
        var child = NewTask("child");
        WorkspaceTask[] tasks = [parent, child];

        Assert.IsNull(TaskHierarchyRules.ValidateParentAssignment("child", "parent", tasks));

        // null / 空 = 脱挂为顶层（既有数据 ParentTaskId 全为 null）。
        Assert.IsNull(TaskHierarchyRules.ValidateParentAssignment("child", null, tasks));
        Assert.IsNull(TaskHierarchyRules.ValidateParentAssignment("child", string.Empty, tasks));
    }

    [TestMethod]
    public void ValidateParentAssignment_RejectsSelfReference()
    {
        var task = NewTask("task-1");

        Assert.AreEqual(
            TaskErrorCode.TaskHierarchyInvalid,
            TaskHierarchyRules.ValidateParentAssignment("task-1", "task-1", [task]));
    }

    [TestMethod]
    public void ValidateParentAssignment_RejectsMissingParent()
    {
        var child = NewTask("child");

        Assert.AreEqual(
            TaskErrorCode.TaskParentNotFound,
            TaskHierarchyRules.ValidateParentAssignment("child", "ghost-parent", [child]));

        // 跨工作区挂载：父不在同工作区集合内 → 同样视为父不存在。
        var foreign = NewTask("foreign", workspaceId: "ws-2");
        Assert.AreEqual(
            TaskErrorCode.TaskParentNotFound,
            TaskHierarchyRules.ValidateParentAssignment("child", "foreign", [child]));
    }

    [TestMethod]
    public void ValidateParentAssignment_RejectsSecondLevelOrCycle()
    {
        var grandParent = NewTask("grand-parent");
        var parent = NewTask("parent", parentTaskId: "grand-parent");
        var child = NewTask("child");
        WorkspaceTask[] tasks = [grandParent, parent, child];

        // 父自身已有父 → 多级挂载被拒（D1 单层；成环同样被此分支拦下）。
        Assert.AreEqual(
            TaskErrorCode.TaskHierarchyInvalid,
            TaskHierarchyRules.ValidateParentAssignment("child", "parent", tasks));

        // 空 taskId（无法标识子卡）→ Invalid。
        Assert.AreEqual(
            TaskErrorCode.TaskHierarchyInvalid,
            TaskHierarchyRules.ValidateParentAssignment(null, "parent", tasks));
        Assert.AreEqual(
            TaskErrorCode.TaskHierarchyInvalid,
            TaskHierarchyRules.ValidateParentAssignment(string.Empty, "parent", tasks));
    }

    // ── 归档/取消拦截（D4）───────────────────────────────────────

    [TestMethod]
    public void HasNonTerminalChildren_UsesTerminalStatusContract()
    {
        var parent = NewTask("parent", status: WorkspaceTaskStatus.InProgress);
        WorkspaceTask[] terminalChildren =
        [
            NewTask("c1", parentTaskId: "parent", status: WorkspaceTaskStatus.Completed),
            NewTask("c2", parentTaskId: "parent", status: WorkspaceTaskStatus.Failed),
            NewTask("c3", parentTaskId: "parent", status: WorkspaceTaskStatus.Cancelled),
            NewTask("c4", parentTaskId: "parent", status: WorkspaceTaskStatus.Archived),
        ];

        Assert.IsFalse(TaskHierarchyRules.HasNonTerminalChildren("parent", [parent, .. terminalChildren]));

        WorkspaceTask[] withOpenChild = [parent, .. terminalChildren, NewTask("c5", parentTaskId: "parent")];
        Assert.IsTrue(TaskHierarchyRules.HasNonTerminalChildren("parent", withOpenChild));
    }

    [TestMethod]
    public void ValidateArchiveOrCancel_FailsClosedUnlessForcedOrAllTerminal()
    {
        var parent = NewTask("parent", status: WorkspaceTaskStatus.InProgress);
        var openChild = NewTask("child", parentTaskId: "parent", status: WorkspaceTaskStatus.Ready);
        var doneChild = NewTask("done-child", parentTaskId: "parent", status: WorkspaceTaskStatus.Completed);

        // 有未终态子卡 → fail-closed 拒绝（D4）。
        Assert.AreEqual(
            TaskErrorCode.TaskHasNonTerminalChildren,
            TaskHierarchyRules.ValidateArchiveOrCancel("parent", [parent, openChild]));

        // 显式 force → 允许（级联由调用方负责）。
        Assert.IsNull(TaskHierarchyRules.ValidateArchiveOrCancel("parent", [parent, openChild], force: true));

        // 全部子卡终态 → 允许。
        Assert.IsNull(TaskHierarchyRules.ValidateArchiveOrCancel("parent", [parent, doneChild]));

        // 无子卡 / 空入参 → 允许（叶子归档不受影响）。
        Assert.IsNull(TaskHierarchyRules.ValidateArchiveOrCancel("parent", [parent]));
        Assert.IsNull(TaskHierarchyRules.ValidateArchiveOrCancel("parent", null));
        Assert.IsNull(TaskHierarchyRules.ValidateArchiveOrCancel(null, [parent, openChild]));
    }

    // ── 只读聚合投影（D3）────────────────────────────────────────

    [TestMethod]
    public void CountChildren_IsReadOnlyAggregateWithoutStatusDerivation()
    {
        var parent = NewTask("parent", status: WorkspaceTaskStatus.Backlog);
        WorkspaceTask[] tasks =
        [
            parent,
            NewTask("c1", parentTaskId: "parent", status: WorkspaceTaskStatus.Completed),
            NewTask("c2", parentTaskId: "parent", status: WorkspaceTaskStatus.InProgress),
            NewTask("c3", parentTaskId: "parent", status: WorkspaceTaskStatus.Archived),
        ];

        var counts = TaskHierarchyRules.CountChildren("parent", tasks);

        Assert.AreEqual(3, counts.Total);
        Assert.AreEqual(2, counts.Terminal);
        Assert.AreEqual(1, counts.NonTerminal);
        Assert.IsFalse(counts.AllTerminal);

        // D3：投影只读——母卡状态与入参对象都不被改写。
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, parent.Status);
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, TaskHierarchyRules.FindTask("parent", tasks)!.Status);
        Assert.IsTrue(TaskHierarchyRules.CountChildren("parent", []).AllTerminal);
    }
}

/// <summary>
/// Stage 1（D1/D4）：父层级错误码的 wire / HTTP 契约单测。
/// <para>
/// 注：契约冻结测试 <c>WorkspaceTaskCoreTests.Enums_Contain_Exactly_Contract_Frozen_Member_Counts</c>
/// 已同步把 TaskErrorCode 计数从 20 改为 23，但该用例在更靠前的 TaskOrigin 断言（既有 stale 期望）
/// 即失败，后面的枚举计数断言不会被执行；因此这里补一个针对新错误码的可执行契约断言。
/// </para>
/// </summary>
[TestClass]
public sealed class TaskHierarchyErrorCodeWireContractTests
{
    [TestMethod]
    public void NewHierarchyErrorCodes_CountIs23_AndWireAndHttpMappingsAreStable()
    {
        // 既有 20 个成员未删除、未改名、未改序，Stage 1 追加 3 个父层级错误码。
        Assert.AreEqual(23, Enum.GetValues<TaskErrorCode>().Length);

        Assert.AreEqual("task.parent_not_found", TaskWireMaps.ErrorCodeToString(TaskErrorCode.TaskParentNotFound));
        Assert.AreEqual("task.hierarchy_invalid", TaskWireMaps.ErrorCodeToString(TaskErrorCode.TaskHierarchyInvalid));
        Assert.AreEqual(
            "task.has_non_terminal_children",
            TaskWireMaps.ErrorCodeToString(TaskErrorCode.TaskHasNonTerminalChildren));

        Assert.AreEqual(404, TaskWireMaps.ErrorCodeToHttpStatus(TaskErrorCode.TaskParentNotFound));
        Assert.AreEqual(422, TaskWireMaps.ErrorCodeToHttpStatus(TaskErrorCode.TaskHierarchyInvalid));
        Assert.AreEqual(409, TaskWireMaps.ErrorCodeToHttpStatus(TaskErrorCode.TaskHasNonTerminalChildren));
    }
}
