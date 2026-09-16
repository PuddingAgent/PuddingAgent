using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Goals;
using PuddingPlatform.Services.Scheduling;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// Stage 2（母/子层级语义落地，D1–D5）：服务层行为测试。
/// <para>
/// 覆盖：挂父校验与显式脱挂（a）、容器不可 claim（b）、容器不进入精炼派发候选（b）、
/// 容器不可 goal_start（b）、D4 归档/取消/删除 fail-closed 与 force 级联（d）、
/// D3 母卡状态不被子卡派生（e）、children_of 过滤与只读聚合计数（f/g）。
/// 并发容量不虚占见 AgentAvailabilityProjectionStoreTests。
/// </para>
/// </summary>
[TestClass]
public sealed class TaskHierarchyStage2Tests
{
    private const string WorkspaceId = "ws-h2";
    private const string AgentId = "agent-1";

    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private SqliteWorkspaceTaskStore _store = null!;
    private WorkspaceTaskAdminService _admin = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "task-hierarchy-stage2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = new SqliteWorkspaceTaskStore(_factory);
        _admin = new WorkspaceTaskAdminService(_factory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ── a. 挂父校验 ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task Create_WithMissingParent_ReturnsParentNotFound()
    {
        var ex = await CaptureAsync(() => _admin.CreateTaskAsync(new TaskAdminCreateRequest
        {
            WorkspaceId = WorkspaceId,
            Title = "孤儿子卡",
            ParentTaskId = "missing-parent",
        }));

        Assert.AreEqual(TaskErrorCode.TaskParentNotFound, ex.ErrorCode);

        // fail-closed：校验先于创建，不留孤儿卡。
        var list = await _admin.ListTasksAsync(new TaskAdminListQuery { WorkspaceId = WorkspaceId });
        Assert.AreEqual(0, list.Items.Count);
    }

    [TestMethod]
    public async Task Update_WithSelfAsParent_ReturnsHierarchyInvalid()
    {
        var task = await CreateAsync("自引用");

        var ex = await CaptureAsync(() => _admin.UpdateTaskAsync(new TaskAdminUpdateRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = task.TaskId,
            ParentTaskId = task.TaskId,
        }));

        Assert.AreEqual(TaskErrorCode.TaskHierarchyInvalid, ex.ErrorCode);
    }

    [TestMethod]
    public async Task Create_WithParentThatAlreadyHasParent_ReturnsHierarchyInvalid()
    {
        var parent = await CreateAsync("母卡");
        var child = await CreateAsync("子卡", parent.TaskId);

        // D1 单层：父自身已有父 ⇒ 多级挂载被拒。
        var ex = await CaptureAsync(() => _admin.CreateTaskAsync(new TaskAdminCreateRequest
        {
            WorkspaceId = WorkspaceId,
            Title = "孙卡",
            ParentTaskId = child.TaskId,
        }));

        Assert.AreEqual(TaskErrorCode.TaskHierarchyInvalid, ex.ErrorCode);
    }

    [TestMethod]
    public async Task Update_ClearParent_DetachesButOmittedParent_KeepsIt()
    {
        var parent = await CreateAsync("母卡");
        var child = await CreateAsync("子卡", parent.TaskId);
        Assert.AreEqual(parent.TaskId, child.ParentTaskId);

        // 不传 parent_task_id = 不变更：仍挂在母卡上。
        var unchanged = await _admin.UpdateTaskAsync(new TaskAdminUpdateRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = child.TaskId,
            Title = "子卡-改名",
        });
        Assert.AreEqual(parent.TaskId, unchanged.Task.ParentTaskId);

        // clear_parent = 显式脱挂。
        var cleared = await _admin.UpdateTaskAsync(new TaskAdminUpdateRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = child.TaskId,
            ClearParent = true,
        });
        Assert.IsNull(cleared.Task.ParentTaskId);
    }

    // ── b. 容器语义 ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task ContainerTask_CannotBeClaimed()
    {
        var parent = await CreateAsync("母卡");
        await CreateAsync("子卡", parent.TaskId);

        var service = new TaskAgentCommandService(_factory, new ManualAlwaysAllowFence());
        var ex = await CaptureAsync(() => service.ClaimAsync(new TaskAgentClaimRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = parent.TaskId,
            AssignmentId = "assignment-1",
            ExpectedVersion = parent.Version,
            AgentId = AgentId,
            ExecutionId = "exec-1",
            SessionId = "conv-1",
            TraceId = "trace-1",
        }));

        Assert.AreEqual(TaskErrorCode.TaskHierarchyInvalid, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ContainerTask_IsNotARefinementCandidate()
    {
        var parent = await CreateAsync("母卡", autoDispatch: true, withRefinementFields: true);
        await CreateAsync("子卡", parent.TaskId);

        var evaluator = new TaskBacklogRefinementEvaluator(
            _factory,
            new Catalog([]),
            Options.Create(new TaskAutoDispatchOptions()));

        var decisions = await evaluator.EvaluateAsync(WorkspaceId, 50);

        // D2：容器母卡不得进入精炼派发候选——候选集合里根本没有它。
        Assert.AreEqual(0, decisions.Count, "容器母卡不应出现在精炼候选集合中");
    }

    [TestMethod]
    public async Task ContainerTask_GoalStartIsRefused()
    {
        var parent = await CreateAsync("母卡");
        await CreateAsync("子卡", parent.TaskId);

        // 容器闸门位于状态白名单之后、评估/派发之前，故 evaluator/starter/options 不会被触达。
        var service = new TaskGoalLaunchService(
            _factory,
            _admin,
            null!,
            null!,
            null!,
            Options.Create(new TaskBoundGoalOptions()),
            NullLogger<TaskGoalLaunchService>.Instance);

        var result = await service.LaunchAsync(new TaskGoalLaunchRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = parent.TaskId,
            AgentId = AgentId,
            ConversationId = "conv-1",
        });

        Assert.IsFalse(result.Started);
        Assert.AreEqual(TaskGoalLaunchCodes.TaskNotDispatchable, result.Code);
    }

    // ── d. D4 归档/取消/删除 ────────────────────────────────────────────

    [TestMethod]
    public async Task Cancel_WithNonTerminalChild_IsRefusedUntilForced()
    {
        var parent = await CreateAsync("母卡");
        var child = await CreateAsync("子卡", parent.TaskId);
        await SetStatusAsync(parent.TaskId, WorkspaceTaskStatus.Ready);

        var ex = await CaptureAsync(() => _admin.ApplyCommandAsync(new TaskAdminCommandRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = parent.TaskId,
            Command = "cancel",
            Reason = "尝试直接取消母卡",
        }));
        Assert.AreEqual(TaskErrorCode.TaskHasNonTerminalChildren, ex.ErrorCode);

        // force 级联：子卡先 cancel，再取消母卡；绝不硬删。
        var forced = await _admin.ApplyCommandAsync(new TaskAdminCommandRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = parent.TaskId,
            Command = "cancel",
            Reason = "显式级联",
            Force = true,
        });
        Assert.AreEqual("Cancelled", forced.Task.Status);

        var cancelledChild = await _store.GetTaskAsync(WorkspaceId, child.TaskId);
        Assert.AreEqual(WorkspaceTaskStatus.Cancelled, cancelledChild!.Status);
    }

    [TestMethod]
    public async Task HardDelete_WithChildren_IsRefused()
    {
        var parent = await CreateAsync("母卡");
        var child = await CreateAsync("子卡", parent.TaskId);

        // 未终态子卡 → has_non_terminal_children（409）。
        var ex = await CaptureAsync(() => _admin.DeleteTaskAsync(WorkspaceId, parent.TaskId));
        Assert.AreEqual(TaskErrorCode.TaskHasNonTerminalChildren, ex.ErrorCode);

        // 子卡终态后硬删仍被拒（false ⇒ cannot_hard_delete），母卡与其子卡都还在。
        await SetStatusAsync(child.TaskId, WorkspaceTaskStatus.Archived);
        Assert.IsFalse(await _admin.DeleteTaskAsync(WorkspaceId, parent.TaskId));
        Assert.IsNotNull(await _store.GetTaskAsync(WorkspaceId, parent.TaskId));
        Assert.IsNotNull(await _store.GetTaskAsync(WorkspaceId, child.TaskId));

        // 存储层同样兜底拒绝（禁止硬删有子卡的任务）。
        Assert.IsFalse(await _store.HardDeleteTaskAsync(WorkspaceId, parent.TaskId));
    }

    // ── e. D3 不变式 ────────────────────────────────────────────────────

    [TestMethod]
    public async Task ChildTransition_NeverDerivesParentStatus()
    {
        var parent = await CreateAsync("母卡");
        var child = await CreateAsync("子卡", parent.TaskId);

        var before = await _store.GetTaskAsync(WorkspaceId, parent.TaskId);

        // 子卡走真实状态流转（Ready → Cancel），母卡不得随之变化。
        await SetStatusAsync(child.TaskId, WorkspaceTaskStatus.Ready);
        var cancelled = await _admin.ApplyCommandAsync(new TaskAdminCommandRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = child.TaskId,
            Command = "cancel",
            Reason = "子卡完成使命",
        });
        Assert.AreEqual("Cancelled", cancelled.Task.Status);

        var after = await _store.GetTaskAsync(WorkspaceId, parent.TaskId);

        // 这就是「状态列失真 = 意图同步失败」的直接防线：母卡 Status 只由人或显式命令设置。
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, after!.Status);
        Assert.AreEqual(before!.Status, after.Status);
        Assert.AreEqual(before.Version, after.Version);
        Assert.AreEqual(before.UpdatedAtUtc, after.UpdatedAtUtc);

        // 只读聚合投影可用于展示，但不参与任何状态派生。
        var detail = await _admin.GetTaskAsync(WorkspaceId, parent.TaskId);
        Assert.IsTrue(detail!.Task.IsContainer);
        Assert.AreEqual(1, detail.Task.ChildTaskCount);
        Assert.AreEqual(1, detail.Task.CompletedChildCount);
        Assert.AreEqual("Backlog", detail.Task.Status);
    }

    // ── f/g. children_of 过滤与只读聚合计数 ─────────────────────────────

    [TestMethod]
    public async Task List_ChildrenOf_FiltersByParent_AndChildSummaryCountsAreReadOnly()
    {
        var parent = await CreateAsync("母卡");
        var childA = await CreateAsync("子卡A", parent.TaskId);
        var childB = await CreateAsync("子卡B", parent.TaskId);
        var leaf = await CreateAsync("独立卡");

        var children = await _admin.ListTasksAsync(new TaskAdminListQuery
        {
            WorkspaceId = WorkspaceId,
            ParentTaskId = parent.TaskId,
        });

        Assert.AreEqual(2, children.Items.Count);
        CollectionAssert.AreEquivalent(
            new[] { childA.TaskId, childB.TaskId },
            children.Items.Select(i => i.TaskId).ToArray());
        Assert.IsTrue(children.Items.All(i => i.ParentTaskId == parent.TaskId));

        await SetStatusAsync(childA.TaskId, WorkspaceTaskStatus.Completed);

        var withSummary = await _admin.ListTasksAsync(new TaskAdminListQuery
        {
            WorkspaceId = WorkspaceId,
            IncludeChildSummary = true,
        });

        var parentItem = withSummary.Items.Single(i => i.TaskId == parent.TaskId);
        Assert.IsTrue(parentItem.IsContainer);
        Assert.AreEqual(2, parentItem.ChildCount);
        Assert.AreEqual(1, parentItem.CompletedChildCount);

        var leafItem = withSummary.Items.Single(i => i.TaskId == leaf.TaskId);
        Assert.IsFalse(leafItem.IsContainer);
        Assert.AreEqual(0, leafItem.ChildCount);
        Assert.AreEqual(0, leafItem.CompletedChildCount);

        // 不带 include_child_summary 时计数为 null（既有 wire 向后兼容），但 is_container 仍准确。
        var withoutSummary = await _admin.ListTasksAsync(new TaskAdminListQuery { WorkspaceId = WorkspaceId });
        var parentPlain = withoutSummary.Items.Single(i => i.TaskId == parent.TaskId);
        Assert.IsNull(parentPlain.ChildCount);
        Assert.IsNull(parentPlain.CompletedChildCount);
        Assert.IsTrue(parentPlain.IsContainer);

        // 聚合是只读投影：跑完列表后母卡自身状态未被回写派生。
        var parentRow = await _store.GetTaskAsync(WorkspaceId, parent.TaskId);
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, parentRow!.Status);
    }

    // ── 帮助 ────────────────────────────────────────────────────────────

    /// <summary>捕获 TaskStoreException（结构化错误码），避免依赖 MSTest 断言 API 版本差异。</summary>
    private static async Task<TaskStoreException> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (TaskStoreException ex)
        {
            return ex;
        }

        Assert.Fail("expected TaskStoreException, but no exception was thrown");
        throw new InvalidOperationException("unreachable");
    }

    private async Task<WorkspaceTask> CreateAsync(
        string title,
        string? parentTaskId = null,
        bool autoDispatch = false,
        bool withRefinementFields = false)
    {
        var result = await _admin.CreateTaskAsync(new TaskAdminCreateRequest
        {
            WorkspaceId = WorkspaceId,
            Title = title,
            Description = withRefinementFields ? "描述" : null,
            AcceptanceCriteria = withRefinementFields ? "验收" : null,
            TaskType = withRefinementFields ? "implementation" : null,
            AutoDispatchEnabled = autoDispatch,
            ParentTaskId = parentTaskId,
        });

        return (await _store.GetTaskAsync(WorkspaceId, result.Task.TaskId))!;
    }

    private async Task SetStatusAsync(string taskId, WorkspaceTaskStatus status)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.WorkspaceTasks.SingleAsync(t => t.WorkspaceId == WorkspaceId && t.TaskId == taskId);
        row.Status = status;
        row.Version += 1;
        await db.SaveChangesAsync();
    }

    private sealed class Catalog(IReadOnlyList<WorkspaceAgentDto> agents) : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult(agents);
    }
}
