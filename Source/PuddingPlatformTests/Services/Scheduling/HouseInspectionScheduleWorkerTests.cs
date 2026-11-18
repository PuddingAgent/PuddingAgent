using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Configuration;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Scheduling;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class HouseInspectionScheduleWorkerTests
{
    private static readonly DateTimeOffset Due = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private SqliteConnection _connection = null!;
    private PlatformDbContextFactory _factory = null!;
    private readonly HouseInspectionTestClock _clock = new(Due.AddMinutes(-1));
    private static readonly PuddingDataPaths Paths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "Pudding's inspection"));

    [TestInitialize]
    public async Task InitializeAsync()
    {
        var dataSource = $"inspection-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dataSource};Mode=Memory;Cache=Shared;Pooling=False";
        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync();
        _factory = new PlatformDbContextFactory(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connectionString).Options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TestCleanup]
    public async Task CleanupAsync() => await _connection.DisposeAsync();

    [TestMethod]
    public async Task DisabledWorkerDoesNotCreateScheduleTables()
    {
        using var worker = CreateWorker(new HouseInspectionScheduleOptions());
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.AreEqual(0L, await CountAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'automation_schedules'"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("ops-agent")]
    public async Task DueScanCreatesOneVisibleTaskAndLedgerEventAcrossWorkerRestart(string? preferredAgentId)
    {
        var settings = Settings(preferredAgentId);
        using var worker = CreateWorker(settings);
        await worker.InitializeAsync(CancellationToken.None);
        Assert.IsEmpty(await worker.ScanAsync(CancellationToken.None));

        _clock.Now = Due.AddSeconds(15);
        using var restarted = CreateWorker(settings);
        await restarted.InitializeAsync(CancellationToken.None);
        var result = (await restarted.ScanAsync(CancellationToken.None)).Single();
        Assert.IsNull(result.Error);
        Assert.AreEqual(1, result.Write?.Created);
        Assert.IsEmpty(await restarted.ScanAsync(CancellationToken.None));

        var tasks = new SqliteWorkspaceTaskStore(_factory);
        var task = (await tasks.QueryTasksAsync(new TaskQuery { WorkspaceId = "default" })).Single();
        Assert.AreEqual("每日只读巡检", task.Title);
        Assert.AreEqual(WorkspaceTaskStatus.Backlog, task.Status);
        Assert.AreEqual(TaskOrigin.AutomationSchedule, task.Origin);
        Assert.AreEqual(preferredAgentId, task.PreferredAgentId);
        Assert.AreEqual(preferredAgentId is not null, task.AutoDispatchEnabled);
        StringAssert.Contains(task.Description!, "Pudding''s inspection");
        StringAssert.Contains(task.Description!, "-SummaryOnly");
        await using var db = await _factory.CreateDbContextAsync();
        var created = await db.TaskEvents.SingleAsync();
        Assert.AreEqual(task.TaskId, created.TaskId);
        Assert.AreEqual(TaskEventType.TaskCreated, created.EventType);
        Assert.AreEqual(1L, await CountAsync("""
            SELECT COUNT(*) FROM automation_runs JOIN workspace_tasks ON automation_runs.task_id = workspace_tasks.task_id
            """));
    }

    [TestMethod]
    public async Task EventWriteFailureRollsBackTaskAndScheduleUntilRetry()
    {
        using var worker = CreateWorker(Settings(null));
        await worker.InitializeAsync(CancellationToken.None);
        await ExecuteAsync("""
            CREATE TRIGGER reject_inspection_event BEFORE INSERT ON task_events
            BEGIN SELECT RAISE(ABORT, 'injected event write failure'); END
            """);
        _clock.Now = Due.AddSeconds(10);

        var failed = (await worker.ScanAsync(CancellationToken.None)).Single();
        Assert.IsInstanceOfType<SqliteException>(failed.Error);
        Assert.AreEqual(0L, await CountAsync("SELECT COUNT(*) FROM workspace_tasks"));
        Assert.AreEqual(0L, await CountAsync("SELECT COUNT(*) FROM automation_runs"));

        await ExecuteAsync("DROP TRIGGER reject_inspection_event");
        Assert.AreEqual(1, (await worker.ScanAsync(CancellationToken.None)).Single().Write?.Created);
        Assert.AreEqual(1L, await CountAsync("SELECT COUNT(*) FROM task_events"));
    }

    private HouseInspectionScheduleWorker CreateWorker(HouseInspectionScheduleOptions settings) =>
        new(_factory, Options.Create(settings), Paths, _clock, NullLogger<HouseInspectionScheduleWorker>.Instance);

    private static HouseInspectionScheduleOptions Settings(string? agent) => new()
    {
        Enabled = true, WorkspaceId = "default", PreferredAgentId = agent,
    };

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class HouseInspectionTestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
