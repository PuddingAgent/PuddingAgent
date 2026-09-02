using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

/// <summary>
/// task_scheduler_intent_outcomes 的 schema 与 store 合同测试（卡 3bd2a4b0 part 1/2）。
/// WiringGuard 用例直接断言组合根 PuddingApplicationInitializer 已接线新 bootstrapper——
/// 防止「表只在测试里手工 bootstrap、生产组合根缺口」的回归（实施方案 §11.2 步骤 7）。
/// </summary>
[TestClass]
public sealed class TaskSchedulerIntentOutcomeStoreTests
{
    [TestMethod]
    public async Task EnsureCreatedAsync_CreatesOutcomeTableWithContractColumns()
    {
        await using var scope = await CreateDatabaseAsync();

        await TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        foreach (var column in new[]
                 {
                     "intent_id", "workspace_id", "task_id", "outcome", "decision_id", "scan_id",
                     "policy_revision", "options_hash", "reason_code", "started_assignment_id",
                     "started_goal_run_id", "created_at_utc",
                 })
        {
            Assert.IsTrue(
                await ColumnExistsAsync(scope.Db, "task_scheduler_intent_outcomes", column),
                $"missing column {column}");
        }
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateDatabaseAsync();

        await TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await TableExistsAsync(scope.Db, "task_scheduler_intent_outcomes"));
    }

    [TestMethod]
    public async Task RecordAsync_WritesReadsAndIsIdempotentOnReplay()
    {
        await using var scope = await CreateDatabaseAsync();
        await TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        var now = DateTimeOffset.Parse("2026-09-02T08:30:00Z");
        // FK(intent_id → task_scheduler_intents) 是生产不变量：outcome 只能引用已领取的 intent。
        var intents = new TaskSchedulerIntentStore(scope.Factory, NullLogger<TaskSchedulerIntentStore>.Instance);
        await intents.EnqueueAsync(new TaskSchedulerIntentEnvelope
        {
            WorkspaceId = "ws",
            Source = TaskSchedulerIntentSources.TaskEvents,
            SourceEventId = 1,
            EventType = "task.ready",
            TaskId = "task-1",
            CreatedAtUtc = now,
        });
        var store = new TaskSchedulerIntentOutcomeStore(scope.Factory);
        // 读取 store 分配的真实 intent_id（envelope 不携带 id）。
        var intentId = await scope.Db.TaskSchedulerIntents
            .AsNoTracking()
            .Select(item => item.IntentId)
            .SingleAsync();
        var outcome = new TaskSchedulerIntentOutcomeRecord
        {
            IntentId = intentId,
            WorkspaceId = "ws",
            TaskId = "task-1",
            Outcome = TaskSchedulerIntentOutcomes.Started,
            DecisionId = "decision-1",
            ScanId = "event-ws-batch1",
            PolicyRevision = 7,
            OptionsHash = "abc123",
            ReasonCode = TaskSchedulerDecisionCodes.Eligible,
            StartedAssignmentId = "asg-1",
            StartedGoalRunId = "goal-1",
            CreatedAtUtc = now,
        };

        var inserted = await store.RecordAsync([outcome]);

        Assert.AreEqual(1, inserted);
        Assert.IsTrue(await store.HasOutcomeAsync(intentId));
        Assert.IsFalse(await store.HasOutcomeAsync("intent-2"));

        // 重放（INSERT OR IGNORE）不产生重复行。
        Assert.AreEqual(0, await store.RecordAsync([outcome]));

        var read = await store.GetOutcomeAsync(intentId);
        Assert.IsNotNull(read);
        Assert.AreEqual(TaskSchedulerIntentOutcomes.Started, read.Outcome);
        Assert.AreEqual("task-1", read.TaskId);
        Assert.AreEqual("decision-1", read.DecisionId);
        Assert.AreEqual("event-ws-batch1", read.ScanId);
        Assert.AreEqual(7, read.PolicyRevision);
        Assert.AreEqual("abc123", read.OptionsHash);
        Assert.AreEqual("asg-1", read.StartedAssignmentId);
        Assert.AreEqual("goal-1", read.StartedGoalRunId);
        Assert.IsNull(await store.GetOutcomeAsync("intent-2"));
    }

    [TestMethod]
    public void PuddingApplicationInitializer_WiresIntentOutcomeSchemaBootstrapper()
    {
        var initializerPath = FindRepoFile(Path.Combine(
            "Source", "PuddingHost", "Hosting", "PuddingApplicationInitializer.cs"));

        // 组合根必须真实接线；缺失即 schema 只有测试自建、生产库永不建表的静默缺口。
        var source = File.ReadAllText(initializerPath);
        Assert.IsTrue(
            source.Contains("TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync", StringComparison.Ordinal),
            "PuddingApplicationInitializer must wire TaskSchedulerIntentOutcomeSchemaBootstrapper.EnsureCreatedAsync");
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent!;
        }

        Assert.IsNotNull(directory, $"repo root with {relativePath} not found from {AppContext.BaseDirectory}");
        return Path.Combine(directory.FullName, relativePath);
    }

    private static async Task<TestDatabaseScope> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new PlatformDbContextFactory(options);
        var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        // FK 引用的父表必须先存在（组合根顺序：intent 表先于 outcome 表）。
        await TaskSchedulerIntentSchemaBootstrapper.EnsureCreatedAsync(db, NullLogger.Instance);
        return new TestDatabaseScope(connection, db, factory);
    }

    private static async Task<bool> TableExistsAsync(DbContext db, string tableName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        command.Parameters.Add(new SqliteParameter("@name", tableName));
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(DbContext db, string tableName, string columnName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed class TestDatabaseScope(
        SqliteConnection connection,
        PlatformDbContext db,
        PlatformDbContextFactory factory) : IAsyncDisposable
    {
        public PlatformDbContext Db { get; } = db;

        public PlatformDbContextFactory Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
