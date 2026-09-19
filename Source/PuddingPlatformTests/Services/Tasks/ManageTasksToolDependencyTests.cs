using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Models;
using PuddingCode.Tasks;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Tasks;
using PuddingRuntime.Services.TaskTools;

namespace PuddingPlatformTests.Services.Tasks;

/// <summary>
/// 看板卡依赖（depends_on_task_ids 读写 + dependencies/dependency_tree 读投影 + children 内联）
/// <b>工具层</b>行为测试：真实 ManageTasksTool → WorkspaceTaskAdminService → TaskDependencyStore → SQLite。
/// <para>
/// 覆盖：(a) 无依赖确定性文案；(b) 单前置 waiting→satisfied 流转 + 后继反向投影；(c) 前置 broken；
/// (d) 自依赖 fail-closed；(e) 成环 fail-closed；(f) create 前置缺失不留孤儿卡；
/// (g) include_children 内联与默认省略；(h) 重复传同边幂等。
/// 与 TaskDependencyStoreTests（store 层）互补：本类从 manage_tasks 的 wire 参数面进入。
/// </para>
/// </summary>
[TestClass]
public sealed class ManageTasksToolDependencyTests
{
    private const string WorkspaceId = "ws-manage-dep";
    private const string AgentId = "agent-1";
    private const string SessionId = "session-1";

    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private WorkspaceTaskAdminService _admin = null!;
    private ManageTasksTool _manage = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "task-manage-dependency", Guid.NewGuid().ToString("N"));
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
    // (a). 无依赖：state=satisfied + 确定性文案
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Get_WithoutDependencies_ReportsSatisfiedAndNoDependenciesText()
    {
        var taskId = await CreateAsync("无依赖卡");

        var output = await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "get",
            ["task_id"] = taskId,
        });

        var deps = output.GetProperty("dependencies");
        Assert.AreEqual("satisfied", deps.GetProperty("state").GetString());
        Assert.AreEqual(0, deps.GetProperty("predecessors").GetArrayLength());
        Assert.AreEqual(0, deps.GetProperty("successors").GetArrayLength());
        Assert.AreEqual("(no dependencies)", output.GetProperty("dependency_tree").GetString());
    }

    // ─────────────────────────────────────────────────────────────
    // (b). 单前置：waiting →（前置完成）→ satisfied；后继反向 waiting
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Create_WithDependency_PersistsEdgeAndEvaluatesWaitingThenSatisfied()
    {
        var predId = await CreateAsync("前置卡");
        var succId = await CreateAsync("后继卡", dependsOn: [predId]);

        // 后继视图：前置未完成 → waiting；树文本含前置行、无后继行。
        var waiting = await ManageGetAsync(succId);
        var waitingDeps = waiting.GetProperty("dependencies");
        Assert.AreEqual("waiting", waitingDeps.GetProperty("state").GetString());
        var preds = waitingDeps.GetProperty("predecessors");
        Assert.AreEqual(1, preds.GetArrayLength());
        Assert.AreEqual(predId, preds[0].GetProperty("task_id").GetString());
        Assert.AreEqual("前置卡", preds[0].GetProperty("title").GetString());
        Assert.AreEqual("Backlog", preds[0].GetProperty("status").GetString());
        Assert.AreEqual("waiting", preds[0].GetProperty("evaluation_state").GetString());

        var waitingTree = waiting.GetProperty("dependency_tree").GetString()!;
        Assert.IsTrue(waitingTree.StartsWith("self: ", StringComparison.Ordinal), waitingTree);
        Assert.IsTrue(waitingTree.Contains($"<- {predId}", StringComparison.Ordinal), waitingTree);
        Assert.IsFalse(waitingTree.Contains("->", StringComparison.Ordinal), waitingTree);

        // 前置视图：successors 反向列出（本卡 Backlog → 对后继是 waiting）。
        var pred = await ManageGetAsync(predId);
        var succs = pred.GetProperty("dependencies").GetProperty("successors");
        Assert.AreEqual(1, succs.GetArrayLength());
        Assert.AreEqual(succId, succs[0].GetProperty("task_id").GetString());
        Assert.AreEqual("waiting", succs[0].GetProperty("evaluation_state").GetString());
        Assert.IsTrue(
            pred.GetProperty("dependency_tree").GetString()!.Contains($"-> {succId}", StringComparison.Ordinal));

        // 前置完成 → satisfied（读侧评估流转）。
        await SetStatusAsync(predId, WorkspaceTaskStatus.Completed);
        var satisfied = await ManageGetAsync(succId);
        Assert.AreEqual("satisfied", satisfied.GetProperty("dependencies").GetProperty("state").GetString());
        Assert.AreEqual(
            "satisfied",
            satisfied.GetProperty("dependencies").GetProperty("predecessors")[0]
                .GetProperty("evaluation_state").GetString());
    }

    // ─────────────────────────────────────────────────────────────
    // (c). 前置 broken（Failed 终态）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Get_WithFailedPredecessor_ReportsBroken()
    {
        var predId = await CreateAsync("会失败的前置");
        var succId = await CreateAsync("被阻塞的后继", dependsOn: [predId]);
        await SetStatusAsync(predId, WorkspaceTaskStatus.Failed);

        var succ = await ManageGetAsync(succId);
        var deps = succ.GetProperty("dependencies");
        Assert.AreEqual("broken", deps.GetProperty("state").GetString());
        Assert.AreEqual("broken", deps.GetProperty("predecessors")[0].GetProperty("evaluation_state").GetString());
        Assert.IsTrue(succ.GetProperty("dependency_tree").GetString()!.Contains("| broken", StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────
    // (d). 自依赖拒绝（fail-closed，不落库）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Update_WithSelfDependency_ReturnsDependencyInvalidWithoutPersisting()
    {
        var taskId = await CreateAsync("自依赖卡");

        var error = await RunErrorAsync(new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["task_id"] = taskId,
            ["depends_on_task_ids"] = new[] { taskId },
        });
        Assert.AreEqual("task.dependency_invalid", error.GetProperty("code").GetString());

        var deps = (await ManageGetAsync(taskId)).GetProperty("dependencies");
        Assert.AreEqual(0, deps.GetProperty("predecessors").GetArrayLength());
    }

    // ─────────────────────────────────────────────────────────────
    // (e). 成环拒绝（fail-closed，环边不落库）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Update_CreatingCycle_ReturnsDependencyInvalid()
    {
        var aId = await CreateAsync("环A");
        var bId = await CreateAsync("环B");

        // A depends_on B（edge B→A）成功。
        await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["task_id"] = aId,
            ["depends_on_task_ids"] = new[] { bId },
        });

        // B depends_on A（edge A→B）成环 → 结构化错误码。
        var error = await RunErrorAsync(new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["task_id"] = bId,
            ["depends_on_task_ids"] = new[] { aId },
        });
        Assert.AreEqual("task.dependency_invalid", error.GetProperty("code").GetString());

        // fail-closed：B 的前置集合仍为空。
        var deps = (await ManageGetAsync(bId)).GetProperty("dependencies");
        Assert.AreEqual(0, deps.GetProperty("predecessors").GetArrayLength());
    }

    // ─────────────────────────────────────────────────────────────
    // (f). create 前置缺失：task.dependency_task_not_found + 不留孤儿卡
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Create_WithMissingPredecessor_ReturnsDependencyTaskNotFoundAndLeavesNoOrphan()
    {
        var error = await RunErrorAsync(new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["title"] = "孤儿卡",
            ["depends_on_task_ids"] = new[] { "missing-pred" },
        });
        Assert.AreEqual("task.dependency_task_not_found", error.GetProperty("code").GetString());

        var list = await RunOkAsync(new Dictionary<string, object?> { ["action"] = "list" });
        Assert.AreEqual(0, list.GetProperty("total").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────
    // (g). include_children 内联子卡（默认省略）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Get_WithIncludeChildren_InlinesChildCardsAndOmitsByDefault()
    {
        var parentId = await CreateAsync("母卡");
        var childId = await CreateAsync("子卡", parentTaskId: parentId);

        var without = await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "get",
            ["task_id"] = parentId,
        });
        Assert.IsFalse(without.TryGetProperty("children", out _), "默认 get 不应内联 children");

        var with = await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "get",
            ["task_id"] = parentId,
            ["include_children"] = true,
        });
        var children = with.GetProperty("children");
        Assert.AreEqual(1, children.GetArrayLength());
        Assert.AreEqual(childId, children[0].GetProperty("task_id").GetString());
        Assert.AreEqual("子卡", children[0].GetProperty("title").GetString());
        Assert.AreEqual("Backlog", children[0].GetProperty("status").GetString());
    }

    // ─────────────────────────────────────────────────────────────
    // (h). 重复传同一条依赖：幂等，不重复落库
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Update_WithDuplicateDependency_IsIdempotent()
    {
        var predId = await CreateAsync("幂等前置");
        var succId = await CreateAsync("幂等后继", dependsOn: [predId]);

        await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["task_id"] = succId,
            ["depends_on_task_ids"] = new[] { predId, predId },
        });

        var deps = (await ManageGetAsync(succId)).GetProperty("dependencies");
        Assert.AreEqual(1, deps.GetProperty("predecessors").GetArrayLength());
    }

    // ─────────────────────────────────────────────────────────────
    // 帮助
    // ─────────────────────────────────────────────────────────────

    private async Task<string> CreateAsync(
        string title,
        string? parentTaskId = null,
        IReadOnlyList<string>? dependsOn = null)
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

        if (dependsOn is not null)
        {
            args["depends_on_task_ids"] = dependsOn;
        }

        var output = await RunOkAsync(args);
        return output.GetProperty("task").GetProperty("task_id").GetString()!;
    }

    /// <summary>get 返回根（TaskAdminGetResult）：dependencies / dependency_tree / children 均在根级，详情在 .task 下。</summary>
    private async Task<JsonElement> ManageGetAsync(string taskId)
    {
        return await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "get",
            ["task_id"] = taskId,
        });
    }

    private async Task<JsonElement> RunOkAsync(Dictionary<string, object?> args)
    {
        var result = await _manage.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = JsonSerializer.Serialize(args),
            Context = Context(),
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Output);
        return JsonDocument.Parse(result.Output!).RootElement;
    }

    private async Task<JsonElement> RunErrorAsync(Dictionary<string, object?> args)
    {
        var result = await _manage.ExecuteAsync(new ToolExecutionRequest
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

    /// <summary>直接改库设置状态（模拟状态机流转结果），避免测试耦合到命令层。</summary>
    private async Task SetStatusAsync(string taskId, WorkspaceTaskStatus status)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.WorkspaceTasks.SingleAsync(t => t.WorkspaceId == WorkspaceId && t.TaskId == taskId);
        row.Status = status;
        row.Version += 1;
        await db.SaveChangesAsync();
    }

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
    };
}
