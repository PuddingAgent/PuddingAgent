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
/// manage_tasks 的 task_type 参数面测试（ADR-092 决策 2 / 看板卡 ef7d6378）：
/// create/update 透传任务类型；缺省不传时行为不变（null → "general"，存储层兜底）。
/// 真实 ManageTasksTool → WorkspaceTaskAdminService → SQLite。
/// </summary>
[TestClass]
public sealed class ManageTasksToolTaskTypeTests
{
    private const string WorkspaceId = "ws-manage-tasktype";
    private const string AgentId = "agent-1";
    private const string SessionId = "session-1";

    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private ManageTasksTool _manage = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PuddingAgent", "task-manage-tasktype", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _manage = new ManageTasksTool(
            new WorkspaceTaskAdminService(_factory),
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
    // create 传 task_type：透传落库，读回等于所传值（归一为小写），而非 general
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Create_WithTaskType_PersistsValueInsteadOfGeneral()
    {
        var output = await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["title"] = "编码卡",
            ["task_type"] = " Coding ",
        });

        Assert.AreEqual("coding", output.GetProperty("task").GetProperty("task_type").GetString());

        var readBack = await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "get",
            ["task_id"] = output.GetProperty("task").GetProperty("task_id").GetString(),
        });
        Assert.AreEqual("coding", readBack.GetProperty("task").GetProperty("task_type").GetString());
    }

    // ─────────────────────────────────────────────────────────────
    // create 不传 task_type：行为与既有完全一致（null → "general"）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Create_WithoutTaskType_KeepsGeneralDefault()
    {
        var output = await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["title"] = "默认类型卡",
        });

        Assert.AreEqual("general", output.GetProperty("task").GetProperty("task_type").GetString());
    }

    // ─────────────────────────────────────────────────────────────
    // update 传 task_type：改写既有卡类型（复用 PatchAsync(taskType) 管道）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Update_WithTaskType_RewritesType()
    {
        var taskId = (await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["title"] = "待改类型卡",
        })).GetProperty("task").GetProperty("task_id").GetString()!;

        await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["task_id"] = taskId,
            ["task_type"] = "Research",
        });

        var readBack = await RunOkAsync(new Dictionary<string, object?>
        {
            ["action"] = "get",
            ["task_id"] = taskId,
        });
        Assert.AreEqual("research", readBack.GetProperty("task").GetProperty("task_type").GetString());
    }

    // ─────────────────────────────────────────────────────────────
    // 帮助
    // ─────────────────────────────────────────────────────────────

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

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
    };
}
