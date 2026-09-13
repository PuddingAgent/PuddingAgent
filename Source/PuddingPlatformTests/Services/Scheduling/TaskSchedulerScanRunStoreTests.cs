using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

/// <summary>
/// task_scheduler_scan_runs 的 schema 与 store 合同测试（P1-B §7.1/§7.2，实施方案用例 10：
/// 空 scan 也持久化；failed scan 可见；重启 running 转 abandoned）。
/// WiringGuard 用例断言组合根 PuddingApplicationInitializer 已接线新 bootstrapper——
/// 防止「表只在测试里手工 bootstrap、生产组合根缺口」的回归（与 IntentOutcome 同模式）。
/// </summary>
[TestClass]
public sealed class TaskSchedulerScanRunStoreTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Parse("2026-09-13T09:30:00Z");

    [TestMethod]
    public async Task EnsureCreatedAsync_CreatesScanRunTableWithContractColumns()
    {
        await using var scope = await CreateDatabaseAsync();

        await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        foreach (var column in new[]
                 {
                     "scan_id", "workspace_id", "trigger", "mode", "policy_revision", "host_boot_id",
                     "status", "started_at_utc", "completed_at_utc", "duration_ms",
                     "availability_refreshed", "idle_agents", "busy_agents", "unknown_agents",
                     "backlog", "candidates", "eligible", "started", "tracked", "repaired",
                     "decision_codes_json", "repair_codes_json", "error_code", "error_summary",
                 })
        {
            Assert.IsTrue(
                await ColumnExistsAsync(scope.Db, "task_scheduler_scan_runs", column),
                $"missing column {column}");
        }

        Assert.IsTrue(
            await IndexExistsAsync(scope.Db, "IX_task_scheduler_scan_runs_workspace_started"),
            "missing index IX_task_scheduler_scan_runs_workspace_started");
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_IsIdempotent()
    {
        await using var scope = await CreateDatabaseAsync();

        await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);

        Assert.IsTrue(await TableExistsAsync(scope.Db, "task_scheduler_scan_runs"));
    }

    [TestMethod]
    public async Task CompleteAsync_PersistsSucceededSummary_EvenForEmptyScan()
    {
        await using var scope = await CreateDatabaseAsync();
        await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        var store = new TaskSchedulerScanRunStore(scope.Factory);

        // §7.2-2：空扫描（candidates=0）也必须落行且终态 succeeded。
        var scanId = await store.TryStartAsync("ws", "recovery_scan", "shadow", 3, "host-a");
        await store.CompleteAsync(scanId, new TaskSchedulerScanRunCompletion
        {
            CompletedAtUtc = StartedAt.AddSeconds(2),
            DurationMs = 2000,
            DecisionCodesJson = TaskSchedulerScanRunStore.SerializeCodeDistribution(
                new Dictionary<string, int>()),
            RepairCodesJson = TaskSchedulerScanRunStore.SerializeCodeDistribution(
                new Dictionary<string, int>()),
        });

        var latest = await store.GetLatestAsync("ws");
        Assert.IsNotNull(latest);
        Assert.AreEqual(scanId, latest.ScanId);
        Assert.AreEqual("recovery_scan", latest.Trigger);
        Assert.AreEqual("shadow", latest.Mode);
        Assert.AreEqual(3, latest.PolicyRevision);
        Assert.AreEqual("host-a", latest.HostBootId);
        Assert.AreEqual(TaskSchedulerScanRunStatuses.Succeeded, latest.Status);
        Assert.AreEqual(0, latest.Candidates);
        Assert.AreEqual(0, latest.Eligible);
        Assert.AreEqual(0, latest.Started);
        Assert.IsNotNull(latest.CompletedAtUtc);
        Assert.AreEqual(2000, latest.DurationMs);
        Assert.AreEqual("{}", latest.DecisionCodesJson);
        Assert.AreEqual("{}", latest.RepairCodesJson);
        Assert.IsNull(latest.ErrorCode);
    }

    [TestMethod]
    public async Task FailAsync_MarksFailedWithVisibleErrorCodeAndSummary()
    {
        await using var scope = await CreateDatabaseAsync();
        await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        var store = new TaskSchedulerScanRunStore(scope.Factory);

        var scanId = await store.TryStartAsync("ws", "manual", "authoritative-single", 7, "host-a");
        await store.FailAsync(scanId, "task_backlog_refinement_unavailable", "boom: refine failed");

        var latest = await store.GetLatestAsync("ws");
        Assert.IsNotNull(latest);
        Assert.AreEqual(TaskSchedulerScanRunStatuses.Failed, latest.Status);
        Assert.AreEqual("task_backlog_refinement_unavailable", latest.ErrorCode);
        Assert.AreEqual("boom: refine failed", latest.ErrorSummary);
        Assert.IsNotNull(latest.CompletedAtUtc);
    }

    [TestMethod]
    public async Task MarkAbandonedAsync_FlipsLeftoverRunningFromOtherBoot_KeepsOthers()
    {
        await using var scope = await CreateDatabaseAsync();
        await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        var store = new TaskSchedulerScanRunStore(scope.Factory);

        // 上一 boot：一行已 succeeded，一行遗留 running。
        var succeededId = await store.TryStartAsync("ws", "recovery_scan", "shadow", 3, "host-old");
        await store.CompleteAsync(succeededId, new TaskSchedulerScanRunCompletion
        {
            CompletedAtUtc = StartedAt,
            DurationMs = 5,
        });
        var leftoverId = await store.TryStartAsync("ws", "recovery_scan", "shadow", 3, "host-old");
        // 当前 boot：遗留 running 不受影响。
        var currentRunningId = await store.TryStartAsync("ws", "manual", "shadow", 3, "host-new");

        var affected = await store.MarkAbandonedAsync("host-new");

        Assert.AreEqual(1, affected);
        // 非当前 boot 的遗留 running → abandoned。
        Assert.AreEqual(
            TaskSchedulerScanRunStatuses.Abandoned,
            await GetStatusAsync(scope.Db, leftoverId));
        // 已 succeeded 的行不受启动恢复影响。
        Assert.AreEqual(
            TaskSchedulerScanRunStatuses.Succeeded,
            await GetStatusAsync(scope.Db, succeededId));
        // 当前 boot 的 running 行不受影响。
        Assert.AreEqual(
            TaskSchedulerScanRunStatuses.Running,
            await GetStatusAsync(scope.Db, currentRunningId));
    }

    [TestMethod]
    public async Task CompleteAsync_RepeatedCall_DoesNotDuplicateRows()
    {
        await using var scope = await CreateDatabaseAsync();
        await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        var store = new TaskSchedulerScanRunStore(scope.Factory);

        var scanId = await store.TryStartAsync("ws", "manual", "shadow", 1, "host-a");
        var completion = new TaskSchedulerScanRunCompletion
        {
            CompletedAtUtc = StartedAt,
            DurationMs = 10,
        };
        await store.CompleteAsync(scanId, completion);
        await store.CompleteAsync(scanId, completion);

        var total = await scope.Db.Database.SqlQuery<long>(
            $"SELECT COUNT(*) AS Value FROM task_scheduler_scan_runs").SingleAsync();
        Assert.AreEqual(1, total);
        var latest = await store.GetLatestAsync("ws");
        Assert.IsNotNull(latest);
        Assert.AreEqual(TaskSchedulerScanRunStatuses.Succeeded, latest.Status);
    }

    [TestMethod]
    public async Task GetLatestAsync_ReturnsMostRecentByStartedAt()
    {
        await using var scope = await CreateDatabaseAsync();
        await TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync(scope.Db);
        var store = new TaskSchedulerScanRunStore(scope.Factory);

        var first = await store.TryStartAsync("ws", "recovery_scan", "shadow", 1, "host-a");
        await store.FailAsync(first, "noop", "first round");
        await Task.Delay(20);
        var second = await store.TryStartAsync("ws", "manual", "shadow", 1, "host-a");

        var latest = await store.GetLatestAsync("ws");

        Assert.IsNotNull(latest);
        Assert.AreEqual(second, latest.ScanId);
        Assert.AreNotEqual(first, latest.ScanId);
    }

    [TestMethod]
    public void PuddingApplicationInitializer_WiresScanRunSchemaBootstrapper()
    {
        var initializerPath = FindRepoFile(Path.Combine(
            "Source", "PuddingHost", "Hosting", "PuddingApplicationInitializer.cs"));

        // 组合根必须真实接线；缺失即 schema 只有测试自建、生产库永不建表的静默缺口。
        var source = File.ReadAllText(initializerPath);
        Assert.IsTrue(
            source.Contains("TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync", StringComparison.Ordinal),
            "PuddingApplicationInitializer must wire TaskSchedulerScanRunSchemaBootstrapper.EnsureCreatedAsync");
    }

    private static async Task<string> GetStatusAsync(DbContext db, string scanId) =>
        await db.Database.SqlQuery<string>(
            $"SELECT status AS Value FROM task_scheduler_scan_runs WHERE scan_id = {scanId}")
            .SingleAsync();

    private static string FindRepoFile(string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
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

    private static async Task<bool> IndexExistsAsync(DbContext db, string indexName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name";
        command.Parameters.Add(new SqliteParameter("@name", indexName));
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
