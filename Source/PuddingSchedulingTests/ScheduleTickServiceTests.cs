using System.Data.Common;
using Microsoft.Data.Sqlite;
using PuddingScheduling;

namespace PuddingSchedulingTests;

[TestClass]
public sealed class ScheduleTickServiceTests
{
    private static readonly DateTimeOffset Due = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private SqliteConnection _database = null!;
    private SqliteScheduleStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _database = new SqliteConnection($"Data Source=tick-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
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
    public async Task RunDueAsync_FiresOnceWhenTimeArrivesAcrossConnections()
    {
        var schedule = Schedule(Due);
        await _store.InsertAsync(schedule, CancellationToken.None);
        var clock = new ScheduleClockForTest(Due.AddSeconds(-1));
        var service = new ScheduleTickService(_store, clock);
        Assert.IsEmpty(await service.RunDueAsync(10, CancellationToken.None));

        clock.UtcNow = Due.AddSeconds(15);
        var first = (await service.RunDueAsync(10, CancellationToken.None)).Single();
        Assert.AreEqual(schedule.Id, first.ScheduleId);
        Assert.AreEqual(new ScheduleTickWrite(true, 1, 0), first.Write);
        Assert.IsNull(first.Error);

        await using var reopened = new SqliteConnection(_database.ConnectionString);
        await reopened.OpenAsync();
        var restarted = new ScheduleTickService(new SqliteScheduleStore(reopened, WriteTaskAsync), clock);
        Assert.IsEmpty(await restarted.RunDueAsync(10, CancellationToken.None));
        Assert.AreEqual(1L, await CountTasksAsync());
    }

    [TestMethod]
    public async Task RunDueAsync_FailureLeavesTheScheduleForRetryAndContinuesTheScan()
    {
        var failing = Schedule(Due.AddDays(-1)) with { CatchUp = CatchUpPolicy.RunOnce };
        var healthy = Schedule(Due);
        await _store.InsertAsync(failing, CancellationToken.None);
        await _store.InsertAsync(healthy, CancellationToken.None);
        var faulting = new SqliteScheduleStore(_database, (record, key, transaction, ct) =>
            record.Id == failing.Id
                ? throw new InvalidOperationException("injected write failure")
                : WriteTaskAsync(record, key, transaction, ct));
        var clock = new ScheduleClockForTest(Due);

        var results = await new ScheduleTickService(faulting, clock).RunDueAsync(10, CancellationToken.None);

        Assert.AreEqual(failing.Id, results[0].ScheduleId);
        Assert.IsInstanceOfType<InvalidOperationException>(results[0].Error);
        Assert.IsNull(results[0].Write);
        Assert.AreEqual(healthy.Id, results[1].ScheduleId);
        Assert.AreEqual(new ScheduleTickWrite(true, 1, 0), results[1].Write);
        var retried = (await new ScheduleTickService(_store, clock).RunDueAsync(10, CancellationToken.None)).Single();
        Assert.AreEqual(failing.Id, retried.ScheduleId);
        Assert.AreEqual(new ScheduleTickWrite(true, 1, 0), retried.Write);
        Assert.AreEqual(2L, await CountTasksAsync());
    }

    private static AutomationScheduleRecord Schedule(DateTimeOffset next) => new(
        Guid.NewGuid(), "Daily inspection", "0 0 * * *", TimeZoneInfo.Utc.Id, "inspect_house", null,
        CatchUpPolicy.Skip, true, next, null, null);

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

    private async Task<long> CountTasksAsync()
    {
        await using var command = _database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM test_tasks;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ScheduleClockForTest(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
