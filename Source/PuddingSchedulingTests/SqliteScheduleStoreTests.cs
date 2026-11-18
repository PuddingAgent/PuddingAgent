using System.Data.Common;
using Microsoft.Data.Sqlite;
using PuddingScheduling;

namespace PuddingSchedulingTests;

[TestClass]
public sealed class SqliteScheduleStoreTests
{
    private static readonly DateTimeOffset Due = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private SqliteConnection _database = null!;
    private SqliteScheduleStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        var settings = new SqliteConnectionStringBuilder
        {
            DataSource = $"schedule-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5,
        };
        _database = new SqliteConnection(settings.ToString());
        await _database.OpenAsync();
        await SqliteScheduleStore.EnsureSchemaAsync(_database, CancellationToken.None);
        await using var command = _database.CreateCommand();
        command.CommandText = "CREATE TABLE test_tasks (id TEXT NOT NULL PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
        _store = new SqliteScheduleStore(_database, WriteTaskAsync);
    }

    [TestCleanup]
    public async Task CleanupAsync() => await _database.DisposeAsync();

    [TestMethod]
    public async Task ListDueAsync_UsesUtcFiltersAndReturnsOldestFirst()
    {
        var old = Schedule(next: Due.AddDays(-1), payload: "{\"source\":\"test\"}");
        var today = Schedule(next: Due.ToOffset(TimeSpan.FromHours(8)));
        await _store.InsertAsync(today, CancellationToken.None);
        await _store.InsertAsync(old, CancellationToken.None);
        await _store.InsertAsync(Schedule(next: Due.AddDays(-2)) with { IsEnabled = false }, CancellationToken.None);
        await _store.InsertAsync(Schedule(next: Due.AddDays(1)), CancellationToken.None);
        await _store.InsertAsync(Schedule(next: Due) with { NextRunAtUtc = null }, CancellationToken.None);

        var found = await _store.ListDueAsync(Due, 10, CancellationToken.None);
        var first = await _store.ListDueAsync(Due, 1, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { old, today }, found.ToArray());
        Assert.AreEqual(TimeSpan.Zero, found[1].NextRunAtUtc!.Value.Offset);
        CollectionAssert.AreEqual(new[] { old }, first.ToArray());
    }

    [TestMethod]
    public async Task TryApplyTickAsync_ConsumesCanonicalKeyAndLinksOnlyOneTask()
    {
        var schedule = Schedule(next: Due);
        await _store.InsertAsync(schedule, CancellationToken.None);
        var plan = new TickPlan([Due, Due.ToOffset(TimeSpan.FromHours(8)).AddSeconds(10)], Due.AddDays(1), 0);

        var result = await _store.TryApplyTickAsync(schedule, plan, Due.AddSeconds(20), CancellationToken.None);

        Assert.AreEqual(new ScheduleTickWrite(true, 1, 1), result);
        Assert.AreEqual(1L, await CountAsync("SELECT COUNT(*) FROM automation_runs"));
        Assert.AreEqual(1L, await CountAsync("SELECT COUNT(*) FROM automation_runs JOIN test_tasks ON task_id = test_tasks.id"));
        var updated = (await _store.ListDueAsync(Due.AddDays(1), 1, CancellationToken.None)).Single();
        Assert.AreEqual(Due.AddDays(1), updated.NextRunAtUtc);
        Assert.AreEqual(Due.AddSeconds(20), updated.LastRunAtUtc);
        Assert.AreEqual(Due, updated.LastFireTimeUtc);
        Assert.AreEqual(1, updated.Revision);
    }

    [TestMethod]
    public async Task TryApplyTickAsync_FailedTaskWriteRollsBackCursorAndFiring()
    {
        var schedule = Schedule(next: Due);
        await _store.InsertAsync(schedule, CancellationToken.None);
        var plan = Plan(schedule, Due);
        var failingStore = new SqliteScheduleStore(_database, async (record, key, transaction, ct) =>
        {
            await WriteTaskAsync(record, key, transaction, ct);
            throw new InvalidOperationException("injected failure after writing the task");
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => failingStore.TryApplyTickAsync(schedule, plan, Due, CancellationToken.None));

        Assert.AreEqual(0L, await CountAsync("SELECT COUNT(*) FROM automation_runs"));
        Assert.AreEqual(0L, await CountAsync("SELECT COUNT(*) FROM test_tasks"));
        Assert.AreEqual(schedule, (await _store.ListDueAsync(Due, 1, CancellationToken.None)).Single());
        Assert.AreEqual(new ScheduleTickWrite(true, 1, 0),
            await _store.TryApplyTickAsync(schedule, plan, Due, CancellationToken.None));
    }

    [TestMethod]
    public async Task TryApplyTickAsync_ConcurrentSnapshotsWriteOnlyOneTask()
    {
        var schedule = Schedule(next: Due);
        await _store.InsertAsync(schedule, CancellationToken.None);
        await using var secondConnection = new SqliteConnection(_database.ConnectionString);
        await secondConnection.OpenAsync();
        var secondStore = new SqliteScheduleStore(secondConnection, WriteTaskAsync);
        var plan = Plan(schedule, Due);

        var results = await Task.WhenAll(
            Task.Run(() => _store.TryApplyTickAsync(schedule, plan, Due, CancellationToken.None)),
            Task.Run(() => secondStore.TryApplyTickAsync(schedule, plan, Due, CancellationToken.None)));

        Assert.AreEqual(1, results.Count(result => result.Applied));
        Assert.AreEqual(1, results.Sum(result => result.Created));
        Assert.AreEqual(1L, await CountAsync("SELECT COUNT(*) FROM test_tasks"));
        Assert.AreEqual(1L, await CountAsync("SELECT COUNT(*) FROM automation_runs"));
    }

    [TestMethod]
    public async Task TryApplyTickAsync_RejectsStaleRevisionEvenIfTheCursorIsIdentical()
    {
        var previous = Schedule(next: Due);
        var edited = previous with { Revision = 1 };
        await _store.InsertAsync(edited, CancellationToken.None);

        var result = await _store.TryApplyTickAsync(previous, Plan(previous, Due), Due, CancellationToken.None);

        Assert.AreEqual(new ScheduleTickWrite(false, 0, 0), result);
        Assert.AreEqual(edited, (await _store.ListDueAsync(Due, 1, CancellationToken.None)).Single());
        Assert.AreEqual(0L, await CountAsync("SELECT COUNT(*) FROM test_tasks"));
    }

    [TestMethod]
    public async Task TryApplyTickAsync_SkipAdvancesWithoutChangingTheLastFire()
    {
        var schedule = Schedule(next: Due) with { LastRunAtUtc = Due.AddDays(-1), LastFireTimeUtc = Due.AddDays(-1) };
        await _store.InsertAsync(schedule, CancellationToken.None);
        var now = Due.AddMinutes(1);

        var result = await _store.TryApplyTickAsync(schedule, Plan(schedule, now), now, CancellationToken.None);

        Assert.AreEqual(new ScheduleTickWrite(true, 0, 0), result);
        Assert.AreEqual(0L, await CountAsync("SELECT COUNT(*) FROM automation_runs"));
        var updated = (await _store.ListDueAsync(Due.AddDays(1), 1, CancellationToken.None)).Single();
        Assert.AreEqual(schedule.LastFireTimeUtc, updated.LastFireTimeUtc);
        Assert.AreEqual(schedule.LastRunAtUtc, updated.LastRunAtUtc);
        Assert.AreEqual(Due.AddDays(1), updated.NextRunAtUtc);
    }

    private static AutomationScheduleRecord Schedule(DateTimeOffset next, string? payload = null) => new(
        Guid.NewGuid(), "Daily inspection", "0 0 * * *", TimeZoneInfo.Utc.Id, "inspect_house", payload,
        CatchUpPolicy.Skip, true, next, null, null);

    private static TickPlan Plan(AutomationScheduleRecord schedule, DateTimeOffset now) =>
        ScheduleTickPlanner.Plan(CronExpression.Parse(schedule.CronExpression), TimeZoneInfo.Utc, schedule.ToState(), now);

    private static async Task<string> WriteTaskAsync(
        AutomationScheduleRecord schedule,
        ScheduleFireKey key,
        DbTransaction transaction,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO test_tasks (id) VALUES ($id);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var command = _database.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
