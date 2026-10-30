using System.Globalization;
using Microsoft.Data.Sqlite;
using PuddingLlmUsage.Contracts;

namespace PuddingLlmUsage.Storage;

/// <summary>
/// <see cref="ILlmUsageStore"/> 的 SQLite 实现。
/// <para>
/// <b>金额存储</b>：一律 TEXT + invariant 字符串。<c>REAL</c> 是二进制浮点，
/// <c>248.55</c> 存进去再读出来可能变成 <c>248.55000000000001</c>，而消耗判定做的是
/// <c>to - from</c> 与 0 比较 —— 末位误差足以把「持平」判成「下降」。
/// </para>
/// <para>
/// <b>连接策略</b>：每次操作新建连接（SQLite 的连接很轻）。持有一个长连接在 WAL 下会累积
/// 读快照，反而更容易看到陈旧数据。
/// </para>
/// </summary>
public sealed class SqliteLlmUsageStore(string databasePath) : ILlmUsageStore
{
    private readonly string _databasePath = string.IsNullOrWhiteSpace(databasePath)
        ? throw new ArgumentException("databasePath 不能为空。", nameof(databasePath))
        : Path.GetFullPath(databasePath);

    private readonly SemaphoreSlim _initLock = new(1, 1);

    private bool _initialized;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS LlmBalanceSampleAttempt (
                    AttemptId      INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProviderId     TEXT NOT NULL,
                    StartedAtUtc   TEXT NOT NULL,
                    CompletedAtUtc TEXT NULL,
                    Status         TEXT NOT NULL,
                    Error          TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_LlmBalanceSampleAttempt_Provider_Started
                    ON LlmBalanceSampleAttempt (ProviderId, StartedAtUtc);

                CREATE TABLE IF NOT EXISTS LlmBalanceCurrencySample (
                    AttemptId      INTEGER NOT NULL,
                    Currency       TEXT NOT NULL,
                    TotalBalance   TEXT NULL,
                    GrantedBalance TEXT NULL,
                    ToppedUpBalance TEXT NULL,
                    ObservedAtUtc  TEXT NOT NULL,
                    PRIMARY KEY (AttemptId, Currency),
                    FOREIGN KEY (AttemptId) REFERENCES LlmBalanceSampleAttempt (AttemptId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_LlmBalanceCurrencySample_Attempt
                    ON LlmBalanceCurrencySample (AttemptId);
                """, cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<long> BeginAttemptAsync(
        string providerId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LlmBalanceSampleAttempt (ProviderId, StartedAtUtc, CompletedAtUtc, Status, Error)
            VALUES ($providerId, $started, NULL, $status, NULL);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$providerId", providerId);
        command.Parameters.AddWithValue("$started", Format(startedAtUtc));
        command.Parameters.AddWithValue("$status", LlmUsageAttemptStatus.Running.ToString());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task CompleteAttemptAsync(
        long attemptId,
        DateTimeOffset completedAtUtc,
        IReadOnlyList<LlmBalanceObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var usable = observations.Where(o => o is not null).ToArray();
        var hasSuccess = usable.Any(o => o.Succeeded);
        var error = usable.FirstOrDefault(o => !o.Succeeded && !string.IsNullOrWhiteSpace(o.Error))?.Error;

        // 一条观测都没有时不能记成 Succeeded：那等于声称"采到了，但什么都没采到"。
        var status = hasSuccess ? LlmUsageAttemptStatus.Succeeded : LlmUsageAttemptStatus.Failed;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE LlmBalanceSampleAttempt
                SET CompletedAtUtc = $completed, Status = $status, Error = $error
                WHERE AttemptId = $attemptId;
                """;
            update.Parameters.AddWithValue("$completed", Format(completedAtUtc));
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            update.Parameters.AddWithValue("$attemptId", attemptId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var observation in usable.Where(o => o.Succeeded))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO LlmBalanceCurrencySample
                    (AttemptId, Currency, TotalBalance, GrantedBalance, ToppedUpBalance, ObservedAtUtc)
                VALUES ($attemptId, $currency, $total, $granted, $toppedUp, $observedAt)
                ON CONFLICT (AttemptId, Currency) DO UPDATE SET
                    TotalBalance = excluded.TotalBalance,
                    GrantedBalance = excluded.GrantedBalance,
                    ToppedUpBalance = excluded.ToppedUpBalance,
                    ObservedAtUtc = excluded.ObservedAtUtc;
                """;
            insert.Parameters.AddWithValue("$attemptId", attemptId);
            insert.Parameters.AddWithValue("$currency", observation.Currency);
            insert.Parameters.AddWithValue("$total", (object?)Format(observation.TotalBalance) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$granted", (object?)Format(observation.GrantedBalance) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$toppedUp", (object?)Format(observation.ToppedUpBalance) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$observedAt", Format(observation.ObservedAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmBalanceObservation>> ReadObservationsAsync(
        string providerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (toUtc < fromUtc)
            throw new ArgumentException("toUtc 不得早于 fromUtc。", nameof(toUtc));

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<LlmBalanceObservation>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 成功尝试的币种级观测。
        await using (var success = connection.CreateCommand())
        {
            success.CommandText = """
                SELECT a.ProviderId, s.Currency, s.TotalBalance, s.GrantedBalance,
                       s.ToppedUpBalance, s.ObservedAtUtc, a.StartedAtUtc
                FROM LlmBalanceCurrencySample s
                JOIN LlmBalanceSampleAttempt a ON a.AttemptId = s.AttemptId
                WHERE a.ProviderId = $providerId
                  AND s.ObservedAtUtc >= $from
                  AND s.ObservedAtUtc <= $to
                ORDER BY s.ObservedAtUtc, s.Currency;
                """;
            success.Parameters.AddWithValue("$providerId", providerId);
            success.Parameters.AddWithValue("$from", Format(fromUtc));
            success.Parameters.AddWithValue("$to", Format(toUtc));

            await using var reader = await success.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var total = ParseNullable(reader.IsDBNull(2) ? null : reader.GetString(2));
                if (total is null)
                    continue; // 无总额的行无法参与计算；它的失败已在尝试级被记录。

                results.Add(LlmBalanceObservation.Success(
                    reader.GetString(0),
                    reader.GetString(1),
                    total.Value,
                    ParseTime(reader.GetString(5)) ?? ParseTime(reader.GetString(6)) ?? fromUtc,
                    ParseNullable(reader.IsDBNull(3) ? null : reader.GetString(3)),
                    ParseNullable(reader.IsDBNull(4) ? null : reader.GetString(4))));
            }
        }

        // 失败/中断尝试还原成 Failure 观测（币种为占位），让缺口在分析中可见。
        await using (var failures = connection.CreateCommand())
        {
            failures.CommandText = """
                SELECT a.ProviderId, a.StartedAtUtc, a.Status, a.Error
                FROM LlmBalanceSampleAttempt a
                WHERE a.ProviderId = $providerId
                  AND a.Status IN ($failed, $interrupted)
                  AND a.StartedAtUtc >= $from
                  AND a.StartedAtUtc <= $to
                ORDER BY a.StartedAtUtc;
                """;
            failures.Parameters.AddWithValue("$providerId", providerId);
            failures.Parameters.AddWithValue("$failed", LlmUsageAttemptStatus.Failed.ToString());
            failures.Parameters.AddWithValue("$interrupted", LlmUsageAttemptStatus.Interrupted.ToString());
            failures.Parameters.AddWithValue("$from", Format(fromUtc));
            failures.Parameters.AddWithValue("$to", Format(toUtc));

            await using var reader = await failures.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var status = reader.GetString(2);
                var error = reader.IsDBNull(3) ? null : reader.GetString(3);
                results.Add(LlmBalanceObservation.Failure(
                    reader.GetString(0),
                    UnknownCurrency.Value,
                    error ?? $"采样未完成（状态 {status}）。",
                    ParseTime(reader.GetString(1)) ?? fromUtc));
            }
        }

        return results
            .OrderBy(o => o.ObservedAtUtc)
            .ThenBy(o => o.Currency, StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<int> MarkRunningAttemptsInterruptedAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LlmBalanceSampleAttempt
            SET Status = $interrupted,
                CompletedAtUtc = $now,
                Error = COALESCE(Error, '进程在上一次运行中异常退出，本次采样未完成。')
            WHERE Status = $running;
            """;
        command.Parameters.AddWithValue("$interrupted", LlmUsageAttemptStatus.Interrupted.ToString());
        command.Parameters.AddWithValue("$running", LlmUsageAttemptStatus.Running.ToString());
        command.Parameters.AddWithValue("$now", Format(nowUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection CreateConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>时间一律存 ISO-8601 往返格式（含偏移），保证字符串排序即时间排序。</summary>
    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Format(decimal? value) =>
        value?.ToString("G29", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    private static decimal? ParseNullable(string? text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
