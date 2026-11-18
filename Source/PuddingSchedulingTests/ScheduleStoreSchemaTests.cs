namespace PuddingSchedulingTests;

using Microsoft.Data.Sqlite;
using PuddingScheduling;

/// <summary>
/// Proves the idempotency guarantee against a real SQLite engine rather than against a hand-written
/// fake. The product's only defence against double-firing is the unique constraint on
/// (schedule_id, scheduled_fire_time_utc); a fake store would prove that *my fake* rejects
/// duplicates, which is worth nothing at runtime.
///
/// The DDL is taken from <see cref="ScheduleStoreSchema"/> — the same constant the host will use —
/// so a schema change that drops the constraint fails these tests instead of silently shipping.
/// </summary>
[TestClass]
public sealed class ScheduleStoreSchemaTests
{
    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        foreach (var sql in ScheduleStoreSchema.All)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private static void InsertRun(
        SqliteConnection connection,
        Guid runId,
        Guid scheduleId,
        DateTimeOffset fireTimeUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {ScheduleStoreSchema.RunsTable}
                (id, schedule_id, scheduled_fire_time_utc, task_id, started_at_utc, status, error)
            VALUES ($id, $sid, $fire, NULL, $started, 0, NULL);
            """;
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        command.Parameters.AddWithValue("$sid", scheduleId.ToString("D"));
        command.Parameters.AddWithValue("$fire", ScheduleFireKey.NormalizeFireTime(fireTimeUtc).ToString("O"));
        command.Parameters.AddWithValue("$started", DateTimeOffset.UnixEpoch.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static long CountRuns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {ScheduleStoreSchema.RunsTable};";
        return (long)command.ExecuteScalar()!;
    }

    [TestMethod]
    public void Schema_AppliesCleanlyToAnEmptyDatabase()
    {
        using var connection = OpenDatabase();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ($a, $b);";
        command.Parameters.AddWithValue("$a", ScheduleStoreSchema.SchedulesTable);
        command.Parameters.AddWithValue("$b", ScheduleStoreSchema.RunsTable);

        Assert.AreEqual(2L, (long)command.ExecuteScalar()!);
    }

    [TestMethod]
    public void Schema_RejectsASecondRowForTheSameScheduleAndFireTime()
    {
        using var connection = OpenDatabase();
        var scheduleId = Guid.NewGuid();
        var fireTime = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

        InsertRun(connection, Guid.NewGuid(), scheduleId, fireTime);

        var error = Assert.ThrowsExactly<SqliteException>(
            () => InsertRun(connection, Guid.NewGuid(), scheduleId, fireTime));

        Assert.AreEqual(19, error.SqliteErrorCode, "Expected SQLITE_CONSTRAINT (19).");
        Assert.AreEqual(1L, CountRuns(connection), "The rejected insert must not have written a row.");
    }

    [TestMethod]
    public void Schema_RejectsDuplicateEvenWhenTheFireTimeIsExpressedWithADifferentOffset()
    {
        // Same instant, different offset: this is the case ScheduleFireKey normalises, and the
        // database has to agree with that normalisation or the constraint is decorative.
        using var connection = OpenDatabase();
        var scheduleId = Guid.NewGuid();
        var utc = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        var shanghai = utc.ToOffset(TimeSpan.FromHours(8));

        InsertRun(connection, Guid.NewGuid(), scheduleId, utc);

        Assert.ThrowsExactly<SqliteException>(
            () => InsertRun(connection, Guid.NewGuid(), scheduleId, shanghai));
        Assert.AreEqual(1L, CountRuns(connection));
    }

    [TestMethod]
    public void Schema_AllowsTheSameFireTimeForADifferentSchedule()
    {
        // Guards against over-constraining: a UNIQUE on scheduled_fire_time_utc alone would make
        // two schedules that run at 08:00 block each other forever.
        using var connection = OpenDatabase();
        var fireTime = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

        InsertRun(connection, Guid.NewGuid(), Guid.NewGuid(), fireTime);
        InsertRun(connection, Guid.NewGuid(), Guid.NewGuid(), fireTime);

        Assert.AreEqual(2L, CountRuns(connection));
    }

    [TestMethod]
    public void Schema_AllowsConsecutiveFiresForTheSameSchedule()
    {
        using var connection = OpenDatabase();
        var scheduleId = Guid.NewGuid();

        InsertRun(connection, Guid.NewGuid(), scheduleId, new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));
        InsertRun(connection, Guid.NewGuid(), scheduleId, new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(2L, CountRuns(connection));
    }
}
