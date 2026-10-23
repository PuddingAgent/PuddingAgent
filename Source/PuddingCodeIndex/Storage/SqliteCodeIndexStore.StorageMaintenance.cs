using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;
using System.Globalization;

namespace PuddingCodeIndex.Storage;

/// <summary>
/// Storage-maintenance surface of the store: a pragma-based accounting snapshot plus batched
/// reclamation of freelist pages. Kept in its own partial file because it is orthogonal to
/// indexing: nothing here reads or writes symbols, files, relations or references.
/// </summary>
public sealed partial class SqliteCodeIndexStore
{
    /// <summary>Short busy timeout for maintenance work: give up rather than queue behind writers.</summary>
    private const int MaintenanceBusyTimeoutMilliseconds = 250;

    /// <summary>
    /// Reads the storage accounting snapshot. Read-only: it opens a separate read-only connection
    /// so that taking a snapshot can never block the indexing writer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current page, freelist, auto-vacuum and file-size counters.</returns>
    public async Task<CodeIndexStorageStats> GetStorageStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateReadOnlyConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var pageSize = await ReadPragmaAsync(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false);
        var pageCount = await ReadPragmaAsync(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
        var freelistCount = await ReadPragmaAsync(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);
        var autoVacuumMode = (int)await ReadPragmaAsync(connection, "PRAGMA auto_vacuum;", cancellationToken).ConfigureAwait(false);

        return new CodeIndexStorageStats(
            pageSize,
            pageCount,
            freelistCount,
            autoVacuumMode,
            FileLengthOrZero(_databasePath),
            FileLengthOrZero(_databasePath + "-wal"));
    }

    /// <summary>
    /// Reclaims at most <paramref name="maxPages"/> freelist pages with one
    /// <c>PRAGMA incremental_vacuum</c>, on a dedicated connection with a short busy timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reported <c>ReclaimedPages</c> is the freelist delta measured across the call, not the
    /// fact that the statement ran: <c>incremental_vacuum</c> returns no rows, and it silently does
    /// nothing when the database is not in incremental mode, when no whole pages are free, or when
    /// a concurrent writer holds the database.
    /// </para>
    /// <para>
    /// Batched on purpose. A single <c>VACUUM</c> takes tens of seconds on a multi-gigabyte index
    /// (measured: 38-74 s on 2.6 GiB) and holds the writer for the whole time, so it is reserved
    /// for an explicit maintenance window.
    /// </para>
    /// </remarks>
    /// <param name="maxPages">Upper bound of pages to release in this batch; must be positive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Before/after snapshots and the pages actually reclaimed.</returns>
    public async Task<CodeIndexReclaimResult> ReclaimFreePagesAsync(
        int maxPages = CodeIndexStoragePolicy.DefaultBatchPages,
        CancellationToken cancellationToken = default)
    {
        if (maxPages <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPages), maxPages, "Batch size must be positive.");

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var before = await GetStorageStatsAsync(cancellationToken).ConfigureAwait(false);
        if (!before.SupportsIncrementalReclaim)
        {
            return new CodeIndexReclaimResult(
                before,
                before,
                0,
                Executed: false,
                SkipReason: $"auto_vacuum is {before.AutoVacuumMode}, not INCREMENTAL ({CodeIndexStorageStats.IncrementalAutoVacuumMode}); "
                    + "PRAGMA incremental_vacuum would be a no-op.");
        }

        await using (var connection = CreateMaintenanceConnection())
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var busy = connection.CreateCommand())
            {
                busy.CommandText = FormattableString.Invariant(
                    $"PRAGMA busy_timeout = {MaintenanceBusyTimeoutMilliseconds};");
                await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = FormattableString.Invariant($"PRAGMA incremental_vacuum({maxPages});");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var after = await GetStorageStatsAsync(cancellationToken).ConfigureAwait(false);

        return new CodeIndexReclaimResult(
            before,
            after,
            before.FreelistCount - after.FreelistCount,
            Executed: true,
            SkipReason: null);
    }

    private SqliteConnection CreateReadOnlyConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

    private SqliteConnection CreateMaintenanceConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        }.ToString());

    private static async Task<long> ReadPragmaAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? 0L
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long FileLengthOrZero(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }
}
