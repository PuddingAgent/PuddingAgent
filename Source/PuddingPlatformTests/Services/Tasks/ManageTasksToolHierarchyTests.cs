using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Tasks;
using PuddingRuntime.Services.TaskTools;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// Stage 3（母/子层级收口）：<b>工具层</b>行为测试（真实 ManageTasksTool → WorkspaceTaskAdminService → SQLite）。
/// <para>
/// 与 TaskHierarchyStage2Tests（服务层直调）互补：本类从 manage_tasks 的 wire 参数面进入，
/// 覆盖 create 的 parent_task_id（成功挂父 / 父不存在 / 违反单层）、list 的 children_of
/// （与 status / priority 叠加）、不传新参数时的<b>向后兼容</b>，以及执行者侧（task_list / task_get）
/// 只读父层级投影（parent_task_id / is_container）确实被填充，外加「执行者侧无父子写参数」的反射守卫（D5）。
/// </para>
/// </summary>
[TestClass]
public sealed class ManageTasksToolHierarchyTests
{
    private const string WorkspaceId = "ws-manage-h3";
    private const string AgentId = "agent-1";
    private const string SessionId = "session-1";

    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private WorkspaceTaskAdminService _admin = null!;
    private ManageTasksTool _manage = null!;
    private TaskAgentCommandService _agentService = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "task-manage-hierarchy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _admin = new WorkspaceTaskAdminService(_factory);
        _manage = new ManageTasksTool(
            _admin,
            Options.Create(new WorkspaceTaskFeatureOptions { Enabled = true }),
            NullLogger<ManageTasksTool>.Instance);
        _agentService = new TaskAgentCommandService(_factory, new ManualAlwaysAllowFence());
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 1(a). create 带 parent_task_id 成功挂父
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ManageCreate_WithParentTaskId_LinksChildAndKeepsParentStatusUntouched()
    {
        var parentId = await CreateAsync("母卡");
        var childId = await CreateAsync("子卡", parentTaskId: parentId);

        // 子卡视图：parent_task_id 落到返回的 task 对象上，且自身不是容器。
        var child = await ManageGetAsync(childId);
        Assert.AreEqual(parentId, child.GetProperty("parent_task_id").GetString());
        Assert.IsFalse(child.GetProperty("is_container").GetBoolean());
        Assert.AreEqual(WorkspaceTaskStatus.Backlog.ToString(), child.GetProperty("status").GetString());

        // 母卡视图：is_container=true + 只读子卡计数（D3 只读投影）。
        var parent = await ManageGetAsync(parentId);
        Assert.IsTrue(parent.GetProperty("is_container").GetBoolean());
        Assert.AreEqual(1, parent.GetProperty("child_task_count").GetInt32());
        Assert.AreEqual(0, parent.GetProperty("completed_child_count").GetInt32());

        // D3：挂父不派生母卡状态/版本语义（母卡仍是 Backlog）。
        Assert.AreEqual("Backlog", parent.GetProperty("status").GetString());

        // list children_of 只返回该母卡的子卡。
        var children = await ManageListAsync(new Dictionary<string, object?> { ["children_of"] = parentId });
        Assert.AreEqual(1, children.GetProperty("total").GetInt32());
        var item = children.GetProperty("items")[0];
        Assert.AreEqual(childId, item.GetProperty("task_id").GetString());
        Assert.AreEqual(parentId, item.GetProperty("parent_task_id").GetString());
    }

    // ─────────────────────────────────────────────────────────────
    // 1(b). create 带不存在的父 → 结构化错误码（不抛异常）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ManageCreate_WithMissingParent_ReturnsStructuredParentNotFound()
    {
        var error = await RunErrorAsync(_manage, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["title"] = "孤儿子卡",
            ["parent_task_id"] = "missing-parent",
        });

        Assert.AreEqual("task.parent_not_found", error.GetProperty("code").GetString());

        // fail-closed：校验先于创建，不留孤儿卡。
        var list = await ManageListAsync();
        Assert.AreEqual(0, list.GetProperty("total").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────
    // 1(c). create 违反单层（父自身已有父）→ task.hierarchy_invalid
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ManageCreate_WithParentThatAlreadyHasParent_ReturnsHierarchyInvalid()
    {
        var parentId = await CreateAsync("母卡");
        var childId = await CreateAsync("子卡", parentTaskId: parentId);

        var error = await RunErrorAsync(_manage, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["title"] = "孙卡",
            ["parent_task_id"] = childId,
        });

        Assert.AreEqual("task.hierarchy_invalid", error.GetProperty("code").GetString());

        // 只有母卡 + 子卡两张，孙卡没有落库。
        var list = await ManageListAsync();
        Assert.AreEqual(2, list.GetProperty("total").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────
    // 1(d). list children_of 过滤 + 与 status / priority 叠加
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ManageList_ChildrenOf_FiltersByParentAndCombinesWithStatusAndPriority()
    {
        var parentId = await CreateAsync("母卡");
        var childA = await CreateAsync("子卡A", parentTaskId: parentId, priority: "p0");
        var childB = await CreateAsync("子卡B", parentTaskId: parentId, priority: "p1");
        await CreateAsync("独立卡");

        // 仅 children_of：2 张子卡，且都带母卡 ID（过滤真的生效，独立卡不在内）。
        var allChildren = await ManageListAsync(new Dictionary<string, object?> { ["children_of"] = parentId });
        Assert.AreEqual(2, allChildren.GetProperty("total").GetInt32());
        CollectionAssert.AreEquivalent(
            new[] { childA, childB },
            allChildren.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("task_id").GetString()).ToArray());
        Assert.IsTrue(allChildren.GetProperty("items").EnumerateArray()
            .All(i => i.GetProperty("parent_task_id").GetString() == parentId));

        // children_of + priority 叠加：只剩 p0 的子卡。
        var p0Only = await ManageListAsync(new Dictionary<string, object?>
        {
            ["children_of"] = parentId,
            ["priority"] = "p0",
        });
        Assert.AreEqual(1, p0Only.GetProperty("total").GetInt32());
        Assert.AreEqual(childA, p0Only.GetProperty("items")[0].GetProperty("task_id").GetString());

        // children_of + status 叠加：子卡B 走到 Completed 后按状态筛只剩它。
        await SetStatusAsync(childB, WorkspaceTaskStatus.Completed);
        var completedOnly = await ManageListAsync(new Dictionary<string, object?>
        {
            ["children_of"] = parentId,
            ["status"] = "Completed",
        });
        Assert.AreEqual(1, completedOnly.GetProperty("total").GetInt32());
        Assert.AreEqual(childB, completedOnly.GetProperty("items")[0].GetProperty("task_id").GetString());

        // 叠加过滤不会把母卡/独立卡卷进来。
        Assert.IsFalse(completedOnly.GetProperty("items").EnumerateArray()
            .Any(i => i.GetProperty("task_id").GetString() != childB));
    }

    // ─────────────────────────────────────────────────────────────
    // 1(e). 向后兼容：不传 parent_task_id / children_of 时行为与改动前一致
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ManageCreateAndList_WithoutHierarchyArgs_KeepsLegacyContract()
    {
        // create 不传 parent_task_id：返回体里 parent_task_id 省略（null 不序列化）、is_container=false、
        // child_task_count / completed_child_count 为 0（无子卡的默认值，非新增过滤）。
        var taskId = await CreateAsync("历史语义任务");
        var created = await ManageGetAsync(taskId);
        Assert.IsFalse(created.TryGetProperty("parent_task_id", out _), "不传父时不得出现 parent_task_id 字段");
        Assert.IsFalse(created.GetProperty("is_container").GetBoolean());
        Assert.AreEqual(0, created.GetProperty("child_task_count").GetInt32());
        Assert.AreEqual(0, created.GetProperty("completed_child_count").GetInt32());

        // 再补两张：一张挂父、一张独立，证明 list 不传 children_of 时「不叠加父过滤」。
        var parentId = await CreateAsync("母卡");
        var childId = await CreateAsync("子卡", parentTaskId: parentId);

        var plain = await ManageListAsync();
        Assert.AreEqual(3, plain.GetProperty("total").GetInt32(), "不传 children_of 时不得按父过滤");
        CollectionAssert.AreEquivalent(
            new[] { taskId, parentId, childId },
            plain.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("task_id").GetString()).ToArray());

        // 列表项：不传 include_child_summary 时计数为 null（字段省略，wire 向后兼容），
        // parent_task_id 仅对真正挂父的卡出现。
        foreach (var item in plain.GetProperty("items").EnumerateArray())
        {
            Assert.IsFalse(item.TryGetProperty("child_count", out _), "不传 include_child_summary 时不得出现 child_count");
            Assert.IsFalse(item.TryGetProperty("completed_child_count", out _), "不传 include_child_summary 时不得出现 completed_child_count");

            var id = item.GetProperty("task_id").GetString();
            if (id == childId)
            {
                Assert.AreEqual(parentId, item.GetProperty("parent_task_id").GetString());
            }
            else
            {
                Assert.IsFalse(item.TryGetProperty("parent_task_id", out _), $"顶层任务 {id} 不得出现 parent_task_id");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2(f). 执行者侧视图包含 parent_task_id / is_container（缺口 A）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecutorListAndGet_ExposeReadOnlyParentAndContainerFlags()
    {
        await SeedMineTaskAsync("task-parent", "执行者可见母卡");
        await SeedMineTaskAsync("task-child", "执行者可见子卡", parentTaskId: "task-parent");

        // task_list（mine 范围）：子卡带 parent_task_id，母卡带 is_container。
        var list = await RunOkAsync(Tool_List(), new Dictionary<string, object?>());
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.AreEqual(2, items.Count);

        var listChild = items.Single(i => i.GetProperty("task_id").GetString() == "task-child");
        Assert.AreEqual("task-parent", listChild.GetProperty("parent_task_id").GetString());
        Assert.IsFalse(listChild.GetProperty("is_container").GetBoolean());

        var listParent = items.Single(i => i.GetProperty("task_id").GetString() == "task-parent");
        Assert.IsTrue(listParent.GetProperty("is_container").GetBoolean());
        Assert.IsFalse(listParent.TryGetProperty("parent_task_id", out _), "顶层母卡不得出现 parent_task_id");

        // task_get：详情同样携带只读父层级投影（含只读子卡计数）。
        var childDetail = await RunOkAsync(Tool_Get(), new Dictionary<string, object?> { ["task_id"] = "task-child" });
        Assert.AreEqual("task-parent", childDetail.GetProperty("task").GetProperty("parent_task_id").GetString());
        Assert.IsFalse(childDetail.GetProperty("task").GetProperty("is_container").GetBoolean());

        var parentDetail = await RunOkAsync(Tool_Get(), new Dictionary<string, object?> { ["task_id"] = "task-parent" });
        var parentTask = parentDetail.GetProperty("task");
        Assert.IsFalse(parentTask.TryGetProperty("parent_task_id", out _));
        Assert.IsTrue(parentTask.GetProperty("is_container").GetBoolean());
        Assert.AreEqual(1, parentTask.GetProperty("child_task_count").GetInt32());
        Assert.AreEqual(0, parentTask.GetProperty("completed_child_count").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────
    // D5 守卫：执行者侧参数面不存在任何父子写参数
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ExecutorToolArgs_ExposeNoParentWriteParameters()
    {
        var executorArgTypes = new[]
        {
            typeof(TaskListArgs),
            typeof(TaskGetArgs),
            typeof(TaskClaimArgs),
            typeof(TaskUpdateArgs),
        };

        foreach (var type in executorArgTypes)
        {
            var offenders = type.GetProperties()
                .Where(p => p.Name.Contains("parent", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToArray();
            Assert.AreEqual(0, offenders.Length, $"{type.Name} 不得暴露父子关系写参数（D5）：{string.Join(",", offenders)}");
        }

        // 正对照：管理者侧（manage_tasks）才是父子关系的唯一写入口。
        Assert.IsNotNull(typeof(ManageTasksArgs).GetProperty("ParentTaskId"));
        Assert.IsNotNull(typeof(ManageTasksArgs).GetProperty("ClearParent"));
        Assert.IsNotNull(typeof(ManageTasksArgs).GetProperty("ChildrenOf"));
    }

    // ─────────────────────────────────────────────────────────────
    // 工具执行帮助
    // ─────────────────────────────────────────────────────────────

    private TaskListTool Tool_List() => new(
        _agentService,
        Options.Create(new WorkspaceTaskFeatureOptions { Enabled = true }),
        NullLogger<TaskListTool>.Instance);

    private TaskGetTool Tool_Get() => new(
        _agentService,
        Options.Create(new WorkspaceTaskFeatureOptions { Enabled = true }),
        NullLogger<TaskGetTool>.Instance);

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
    };

    private static async Task<JsonElement> RunOkAsync<TArgs>(PuddingToolBase<TArgs> tool, Dictionary<string, object?> args)
        where TArgs : class
    {
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = JsonSerializer.Serialize(args),
            Context = Context(),
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Output);
        return JsonDocument.Parse(result.Output!).RootElement;
    }

    private static async Task<JsonElement> RunErrorAsync<TArgs>(PuddingToolBase<TArgs> tool, Dictionary<string, object?> args)
        where TArgs : class
    {
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = JsonSerializer.Serialize(args),
            Context = Context(),
        });

        Assert.IsFalse(result.Success, "expected failure but the tool succeeded");
        Assert.IsNotNull(result.Error);
        Assert.IsTrue(result.Error!.StartsWith('{'), $"错误体应为统一 JSON 协议（§7），实际：{result.Error}");
        return JsonDocument.Parse(result.Error!).RootElement.GetProperty("error");
    }

    private async Task<string> CreateAsync(string title, string? parentTaskId = null, string? priority = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["title"] = title,
        };
        if (parentTaskId is not null)
        {
            args["parent_task_id"] = parentTaskId;
        }

        if (priority is not null)
        {
            args["priority"] = priority;
        }

        var output = await RunOkAsync(_manage, args);
        return output.GetProperty("task").GetProperty("task_id").GetString()!;
    }

    private async Task<JsonElement> ManageGetAsync(string taskId)
    {
        var output = await RunOkAsync(_manage, new Dictionary<string, object?>
        {
            ["action"] = "get",
            ["task_id"] = taskId,
        });
        return output.GetProperty("task");
    }

    private async Task<JsonElement> ManageListAsync(Dictionary<string, object?>? extra = null)
    {
        var args = new Dictionary<string, object?> { ["action"] = "list" };
        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                args[pair.Key] = pair.Value;
            }
        }

        return await RunOkAsync(_manage, args);
    }

    /// <summary>直接改库设置状态（模拟状态机流转结果），避免测试耦合到命令层。</summary>
    private async Task SetStatusAsync(string taskId, WorkspaceTaskStatus status)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.WorkspaceTasks.SingleAsync(t => t.WorkspaceId == WorkspaceId && t.TaskId == taskId);
        row.Status = status;
        row.Version += 1;
        await db.SaveChangesAsync();
    }

    /// <summary>播种一张「mine」任务（active assignment 属于 AgentId），供执行者侧工具读取。</summary>
    private async Task SeedMineTaskAsync(
        string taskId,
        string title,
        string? parentTaskId = null,
        WorkspaceTaskStatus status = WorkspaceTaskStatus.Backlog)
    {
        var now = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        await using var db = await _factory.CreateDbContextAsync();
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = taskId,
            WorkspaceId = WorkspaceId,
            Title = title,
            Status = status,
            Priority = TaskPriority.P3,
            ExecutionWindow = TaskExecutionWindow.Anytime,
            ActiveAssignmentId = "assignment-" + taskId,
            ParentTaskId = parentTaskId,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
        {
            AttemptId = "assignment-" + taskId,
            TaskId = taskId,
            WorkspaceId = WorkspaceId,
            AgentId = AgentId,
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.InProgress,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ActiveAtUtc = now,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>播种一张「Assigned + 活跃 assignment」的任务，供服务端 claim 路径使用。</summary>
    private async Task SeedAssignedTaskAsync(string taskId, string title, string? parentTaskId = null)
    {
        var now = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        await using var db = await _factory.CreateDbContextAsync();
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = taskId,
            WorkspaceId = WorkspaceId,
            Title = title,
            Status = WorkspaceTaskStatus.Assigned,
            Priority = TaskPriority.P3,
            ExecutionWindow = TaskExecutionWindow.Anytime,
            ActiveAssignmentId = "assignment-" + taskId,
            ParentTaskId = parentTaskId,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
        {
            AttemptId = "assignment-" + taskId,
            TaskId = taskId,
            WorkspaceId = WorkspaceId,
            AgentId = AgentId,
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.Assigned,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ActiveAtUtc = now,
        });
        await db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────
    // 2(g). 执行者侧 mutation 结果（task_claim / task_update）确实被填充父层级字段（缺口 A）
    // ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExecutorMutationResult_CarriesReadOnlyParentAndContainerFlags()
    {
        await SeedAssignedTaskAsync("claim-parent", "认领用母卡");
        await SeedAssignedTaskAsync("claim-child", "认领用子卡", parentTaskId: "claim-parent");

        // 子卡 claim：mutation 结果带父标识（非 null），且自身不是容器。
        var childResult = await _agentService.ClaimAsync(new TaskAgentClaimRequest
        {
            WorkspaceId = WorkspaceId,
            TaskId = "claim-child",
            AssignmentId = "assignment-claim-child",
            ExpectedVersion = 1,
            AgentId = AgentId,
        });
        Assert.AreEqual("claim-parent", childResult.ParentTaskId);
        Assert.IsFalse(childResult.IsContainer);

        // 容器母卡不可 claim（只读容器标记与执行入口一致：容器不是可执行的工位）。
        var thrown = false;
        try
        {
            await _agentService.ClaimAsync(new TaskAgentClaimRequest
            {
                WorkspaceId = WorkspaceId,
                TaskId = "claim-parent",
                AssignmentId = "assignment-claim-parent",
                ExpectedVersion = 1,
                AgentId = AgentId,
            });
        }
        catch (TaskStoreException)
        {
            thrown = true;
        }

        Assert.IsTrue(thrown, "容器母卡不应可被 claim");
    }
}
