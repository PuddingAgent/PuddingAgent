using System.Data.Common;
using System.Globalization;

namespace PuddingScheduling;

/// <summary>
/// SQLite storage over a caller-owned open connection. The task writer must use the supplied
/// transaction for all writes, so SQLite can roll back a task, its firing and the cursor together.
/// Use a separate connection for concurrent ticks.
/// </summary>
public sealed class SqliteScheduleStore(
    DbConnection connection,
    Func<AutomationScheduleRecord, ScheduleFireKey, DbTransaction, CancellationToken, Task<string>> createTask)
    : IScheduleStore
{
    private const int TaskCreated = 0;

    public static async Task EnsureSchemaAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var statement in ScheduleStoreSchema.All)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task InsertAsync(AutomationScheduleRecord schedule, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            INSERT INTO automation_schedules
                (id, name, cron_expression, time_zone_id, handler, payload_json, catch_up_policy,
                 is_enabled, next_run_at_utc, last_run_at_utc, last_fire_time_utc, revision)
            VALUES
                ($id, $name, $cron, $zone, $handler, $payload, $policy,
                 $enabled, $next, $lastRun, $lastFire, $revision)
            """, null, ("$id", schedule.Id.ToString("D")), ("$name", schedule.Name),
            ("$cron", schedule.CronExpression), ("$zone", schedule.TimeZoneId), ("$handler", schedule.Handler),
            ("$payload", schedule.PayloadJson), ("$policy", (int)schedule.CatchUp), ("$enabled", schedule.IsEnabled),
            ("$next", FormatUtc(schedule.NextRunAtUtc)), ("$lastRun", FormatUtc(schedule.LastRunAtUtc)),
            ("$lastFire", FormatUtc(schedule.LastFireTimeUtc)), ("$revision", schedule.Revision));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AutomationScheduleRecord>> ListDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var command = CreateCommand("""
            SELECT id, name, cron_expression, time_zone_id, handler, payload_json, catch_up_policy,
                   is_enabled, next_run_at_utc, last_run_at_utc, last_fire_time_utc, revision
            FROM automation_schedules
            WHERE is_enabled = 1 AND next_run_at_utc <= $now
            ORDER BY next_run_at_utc, id
            LIMIT $limit
            """, null, ("$now", FormatUtc(nowUtc)), ("$limit", limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var schedules = new List<AutomationScheduleRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            schedules.Add(new AutomationScheduleRecord(
                Id: reader.GetGuid(0),
                Name: reader.GetString(1),
                CronExpression: reader.GetString(2),
                TimeZoneId: reader.GetString(3),
                Handler: reader.GetString(4),
                PayloadJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                CatchUp: (CatchUpPolicy)reader.GetInt32(6),
                IsEnabled: reader.GetBoolean(7),
                NextRunAtUtc: ReadTimestamp(reader, 8),
                LastRunAtUtc: ReadTimestamp(reader, 9),
                LastFireTimeUtc: ReadTimestamp(reader, 10),
                Revision: reader.GetInt64(11)));
        }
        return schedules;
    }

    public async Task<ScheduleTickWrite> TryApplyTickAsync(
        AutomationScheduleRecord schedule,
        TickPlan plan,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (plan.NextRunAtUtc <= nowUtc)
            throw new ArgumentException("The next cursor must be in the future.", nameof(plan));

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var scheduleId = schedule.Id.ToString("D");
        await using var advance = CreateCommand("""
            UPDATE automation_schedules
            SET next_run_at_utc = $next, revision = revision + 1
            WHERE id = $id AND revision = $revision AND is_enabled = 1 AND next_run_at_utc = $expected
                AND next_run_at_utc <= $now
            """, transaction, ("$next", FormatUtc(plan.NextRunAtUtc)), ("$id", scheduleId),
            ("$revision", schedule.Revision), ("$expected", FormatUtc(schedule.NextRunAtUtc)),
            ("$now", FormatUtc(nowUtc)));
        if (await advance.ExecuteNonQueryAsync(cancellationToken) == 0)
            return new ScheduleTickWrite(false, 0, 0);

        var created = 0;
        var alreadyRecorded = 0;
        DateTime? lastFire = null;
        foreach (var fireTime in plan.FireTimes)
        {
            var key = ScheduleFireKey.Create(schedule.Id, fireTime);
            var runId = Guid.NewGuid().ToString("D");
            await using var reserve = CreateCommand("""
                INSERT INTO automation_runs (id, schedule_id, scheduled_fire_time_utc, started_at_utc, status)
                VALUES ($run, $schedule, $fire, $now, $status)
                ON CONFLICT (schedule_id, scheduled_fire_time_utc) DO NOTHING
                """, transaction, ("$run", runId), ("$schedule", scheduleId),
                ("$fire", key.FireTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$now", FormatUtc(nowUtc)), ("$status", TaskCreated));
            if (await reserve.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                alreadyRecorded++;
                continue;
            }

            var taskId = await createTask(schedule, key, transaction, cancellationToken);
            if (string.IsNullOrWhiteSpace(taskId))
                throw new InvalidOperationException("The scheduled task writer returned an empty task ID.");

            await using var link = CreateCommand("""
                UPDATE automation_runs SET task_id = $task WHERE id = $run
                """, transaction, ("$task", taskId), ("$run", runId));
            await link.ExecuteNonQueryAsync(cancellationToken);
            created++;
            if (lastFire is null || key.FireTimeUtc > lastFire)
                lastFire = key.FireTimeUtc;
        }

        if (lastFire is { } firedAt)
        {
            await using var updateLast = CreateCommand("""
                UPDATE automation_schedules SET last_run_at_utc = $now, last_fire_time_utc = $fire WHERE id = $id
                """, transaction, ("$now", FormatUtc(nowUtc)), ("$fire", firedAt.ToString("O", CultureInfo.InvariantCulture)),
                ("$id", scheduleId));
            await updateLast.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ScheduleTickWrite(true, created, alreadyRecorded);
    }

    private DbCommand CreateCommand(string sql, DbTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static string? FormatUtc(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadTimestamp(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTimeOffset.Parse(
            reader.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
