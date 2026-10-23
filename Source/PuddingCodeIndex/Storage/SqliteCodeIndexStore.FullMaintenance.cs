using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;
using System.Globalization;

namespace PuddingCodeIndex.Storage;

/// <summary>
/// Explicit, destructive maintenance of one index database: back up, switch to incremental
/// auto-vacuum, rewrite the file, then verify the result.
/// <para>
/// This is deliberately <b>not</b> wired into startup or into a background loop. Reclaiming a
/// multi-gigabyte index with a full <c>VACUUM</c> takes tens of seconds (measured 38-74 s on
/// 2.6 GiB) and holds the single writer for the whole time, so it belongs to an operator-chosen
/// window. Batched reclamation, which is safe to run in the background, lives in
/// <c>SqliteCodeIndexStore.StorageMaintenance.cs</c> instead.
/// </para>
/// </summary>
public sealed partial class SqliteCodeIndexStore
{
    /// <summary>Long busy timeout for maintenance: wait for a clean window rather than fail fast.</summary>
    private const int FullMaintenanceBusyTimeoutMilliseconds = 5000;

    /// <summary>
    /// Runs one full maintenance pass and reports the evidence.
    /// <para>
    /// Order matters and is asserted: checkpoint must be idle before the rewrite, the backup must
    /// verify before the rewrite touches anything, and the row counts must be identical afterwards.
    /// The caller is responsible for stopping writers first; this method does not know about them.
    /// </para>
    /// </summary>
    /// <param name="backupPath">Destination for the pre-maintenance backup; overwritten if present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The maintenance report; check <see cref="CodeIndexMaintenanceReport.Succeeded"/>.</returns>
    public async Task<CodeIndexMaintenanceReport> RunFullMaintenanceAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var before = await GetStorageStatsAsync(cancellationToken).ConfigureAwait(false);
        var rowsBefore = await ReadRowCountsAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<string>();

        // 1. Backup first, and only proceed once it verifies. Copying a live WAL database file by
        //    hand is not a consistent snapshot; the backup API is.
        var backup = await CreateVerifiedBackupAsync(backupPath, rowsBefore, cancellationToken)
            .ConfigureAwait(false);

        // 2. Rewrite. Every statement here must run outside an explicit transaction.
        await using (var connection = CreateMaintenanceConnection())
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteMaintenanceAsync(
                connection,
                FormattableString.Invariant($"PRAGMA busy_timeout = {FullMaintenanceBusyTimeoutMilliseconds};"),
                cancellationToken).ConfigureAwait(false);

            await ExecuteMaintenanceAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken)
                .ConfigureAwait(false);

            var checkpointBefore = await CheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
            if (checkpointBefore != (0L, 0L, 0L))
            {
                // A non-idle checkpoint means a reader or writer is still attached; rewriting now
                // would either fail or produce a result nobody can trust.
                failures.Add(
                    $"pre-VACUUM checkpoint was not idle: busy={checkpointBefore.Busy}, "
                    + $"log={checkpointBefore.Log}, checkpointed={checkpointBefore.Checkpointed}");
            }

            if (checkpointBefore == (0L, 0L, 0L))
            {
                await ExecuteMaintenanceAsync(connection, "PRAGMA auto_vacuum = INCREMENTAL;", cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteMaintenanceAsync(connection, "VACUUM;", cancellationToken).ConfigureAwait(false);
                await ExecuteMaintenanceAsync(connection, "PRAGMA optimize;", cancellationToken).ConfigureAwait(false);
                await CheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }

        // 3. Verify the result against the snapshot, never against "it ran".
        var after = await GetStorageStatsAsync(cancellationToken).ConfigureAwait(false);
        var rowsAfter = await ReadRowCountsAsync(cancellationToken).ConfigureAwait(false);

        if (!backup.Verified)
            failures.Add("backup did not verify");

        if (after.AutoVacuumMode != CodeIndexStorageStats.IncrementalAutoVacuumMode)
        {
            failures.Add(
                $"auto_vacuum is {after.AutoVacuumMode}, expected "
                + $"{CodeIndexStorageStats.IncrementalAutoVacuumMode}");
        }

        if (after.FreelistCount != 0)
            failures.Add($"freelist_count is {after.FreelistCount}, expected 0 after a full VACUUM");

        foreach (var (table, countBefore) in rowsBefore)
        {
            if (!rowsAfter.TryGetValue(table, out var countAfter))
                failures.Add($"table {table} disappeared during maintenance");
            else if (countAfter != countBefore)
                failures.Add($"table {table} row count changed: {countBefore} -> {countAfter}");
        }

        foreach (var table in rowsAfter.Keys)
        {
            if (!rowsBefore.ContainsKey(table))
                failures.Add($"table {table} appeared during maintenance");
        }

        // The file length must equal the page count the engine reports; asserting an exact byte
        // count measured on some other copy would be wrong, because the pointer map changes it.
        var expectedBytes = after.PageCount * after.PageSize;
        if (after.DatabaseFileBytes != expectedBytes)
        {
            failures.Add(
                $"file length {after.DatabaseFileBytes} != page_count*page_size {expectedBytes}");
        }

        var reclaimed = Math.Max(0L, before.DatabaseFileBytes - after.DatabaseFileBytes);

        return new CodeIndexMaintenanceReport(
            BackupPath: backupPath,
            BackupBytes: backup.Bytes,
            BackupVerified: backup.Verified,
            Before: before,
            After: after,
            RowCountsBefore: rowsBefore,
            RowCountsAfter: rowsAfter,
            ReclaimedBytes: reclaimed,
            VerificationFailures: failures);
    }

    private async Task<(long Bytes, bool Verified, IReadOnlyList<string> Failures)> CreateVerifiedBackupAsync(
        string backupPath,
        IReadOnlyDictionary<string, long> expectedRows,
        CancellationToken cancellationToken)
    {
        var fullBackupPath = Path.GetFullPath(backupPath);
        var directory = Path.GetDirectoryName(fullBackupPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // A stale backup must never be mistaken for the one produced by this run.
        if (File.Exists(fullBackupPath))
            File.Delete(fullBackupPath);

        await using (var source = CreateReadOnlyConnection())
        await using (var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = fullBackupPath,
                Pooling = false,
            }.ToString()))
        {
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);

            // The backup API copies page by page and yields a consistent snapshot even while the
            // source is in WAL mode; copying db/-wal/-shm by hand does not.
            source.BackupDatabase(destination);
        }

        var failures = new List<string>();

        await using var check = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = fullBackupPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        await check.OpenAsync(cancellationToken).ConfigureAwait(false);

        var integrity = await ExecuteScalarStringAsync(check, "PRAGMA integrity_check;", cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            failures.Add($"backup integrity_check returned '{integrity}'");

        foreach (var (table, expected) in expectedRows)
        {
            var actual = await ExecuteScalarLongAsync(
                check,
                $"SELECT count(*) FROM \"{table}\";",
                cancellationToken).ConfigureAwait(false);

            if (actual != expected)
                failures.Add($"backup table {table} row count: {actual} != {expected}");
        }

        var bytes = FileLengthOrZero(fullBackupPath);
        if (bytes <= 0)
            failures.Add("backup file is empty");

        return (bytes, failures.Count == 0, failures);
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadRowCountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateReadOnlyConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                tables.Add(reader.GetString(0));
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            counts[table] = await ExecuteScalarLongAsync(
                connection,
                $"SELECT count(*) FROM \"{table}\";",
                cancellationToken).ConfigureAwait(false);
        }

        return counts;
    }

    private static async Task ExecuteMaintenanceAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(long Busy, long Log, long Checkpointed)> CheckpointAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return (-1L, -1L, -1L);

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
