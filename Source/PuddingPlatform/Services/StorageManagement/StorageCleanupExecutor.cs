using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>单轮执行结果。</summary>
public sealed record StorageCleanupRoundResult
{
    public bool AllActionsComplete { get; init; }
    public bool NeedsConfirmation { get; init; }
    public bool Cancelled { get; init; }
    public long RemainingRowsEstimate { get; init; }
    public bool RemainingTruncated { get; init; }
}

/// <summary>
/// ADR-076 §5.4 小批清理执行器：100 行/批、批间 250ms 让步、单目标单轮 200 批、
/// SQLite busy 立即退避重试、单轮 slice 30 秒。删除按 (rowid > cursor) 续行；
/// 证据表先归档后删除；SQL 全部由白名单目录翻译生成，参数化 cutoff。
/// </summary>
public sealed class StorageCleanupExecutor
{
    private const int CandidateProbeLimit = 5_000;
    private const int BusyRetryLimit = 8;
    private static readonly TimeSpan BusyBackoff = TimeSpan.FromMilliseconds(500);

    private readonly PuddingDataPaths _paths;
    private readonly RetentionArchiveWriter _archiveWriter;
    private readonly IEnumerable<IStorageDerivedTargetHandler> _derivedHandlers;
    private readonly ILogger<StorageCleanupExecutor> _logger;

    public StorageCleanupExecutor(
        PuddingDataPaths paths,
        RetentionArchiveWriter archiveWriter,
        IEnumerable<IStorageDerivedTargetHandler> derivedHandlers,
        ILogger<StorageCleanupExecutor> logger)
    {
        _paths = paths;
        _archiveWriter = archiveWriter;
        _derivedHandlers = derivedHandlers;
        _logger = logger;
    }

    /// <summary>有界候选探测：计数上限内精确，超出返回 truncated（不做无界 COUNT(*)）。</summary>
    public static async Task<(long Count, bool Truncated)> ProbeCandidatesAsync(
        SqliteConnection connection, string table, string timestampColumn, string cutoffString, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM (SELECT 1 FROM {QuoteIdentifier(table)} " +
            $"WHERE {QuoteIdentifier(timestampColumn)} < $cutoff LIMIT {CandidateProbeLimit})";
        command.Parameters.AddWithValue("$cutoff", cutoffString);
        command.CommandTimeout = 15;
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        return (count, count >= CandidateProbeLimit);
    }

    /// <summary>轮末剩余探测：从当前 cursor 起、按动作语义（清字段模式排除已清空行）。</summary>
    private async Task<(long Count, bool Truncated)> ProbeRemainingAsync(
        StorageCleanupAction action, StorageCleanupJob job, CancellationToken ct)
    {
        var databasePath = ResolveDatabasePath(action);
        if (!File.Exists(databasePath) || string.IsNullOrEmpty(action.Table) || string.IsNullOrEmpty(action.TimestampColumn))
            return (0, false);

        await using var connection = await OpenReadWriteConnectionAsync(databasePath, ct);
        if (!await TableExistsAsync(connection, action.Table!, ct))
            return (0, false);

        var cursor = job.Cursors.TryGetValue(action.TargetId, out var value) && value != "done"
            ? value
            : "0";
        var clearCondition = action.ClearColumns is { Length: > 0 }
            ? $" AND ({string.Join(" OR ", action.ClearColumns.Select(c => $"{QuoteIdentifier(c)} IS NOT NULL"))})"
            : string.Empty;

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM (SELECT 1 FROM {QuoteIdentifier(action.Table!)} " +
            $"WHERE {QuoteIdentifier(action.TimestampColumn!)} < $cutoff AND rowid > $cursor{clearCondition} " +
            $"LIMIT {CandidateProbeLimit})";
        command.Parameters.AddWithValue("$cutoff", job.CutoffUtc.ToString("O"));
        command.Parameters.AddWithValue("$cursor", long.TryParse(cursor, out var parsed) ? parsed : 0L);
        command.CommandTimeout = 15;
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        return (count, count >= CandidateProbeLimit);
    }

    public async Task<(long Count, bool Truncated)> ProbeCandidatesAsync(
        StorageCleanupAction action, DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var databasePath = ResolveDatabasePath(action);
        if (!File.Exists(databasePath) || string.IsNullOrEmpty(action.Table) || string.IsNullOrEmpty(action.TimestampColumn))
            return (0, false);

        await using var connection = await OpenReadWriteConnectionAsync(databasePath, ct);
        if (!await TableExistsAsync(connection, action.Table!, ct))
            return (0, false);
        return await ProbeCandidatesAsync(
            connection, action.Table!, action.TimestampColumn!, cutoffUtc.ToString("O"), ct);
    }

    /// <summary>执行作业一轮（每动作小批有界；返回是否全部完成）。</summary>
    public async Task<StorageCleanupRoundResult> ExecuteRoundAsync(
        StorageCleanupJob job,
        CancellationToken ct)
    {
        var sliceWatch = Stopwatch.StartNew();
        var sliceDeadline = TimeSpan.FromSeconds(Math.Max(5, job.Budget.SliceSeconds));
        var allComplete = true;
        long remainingEstimate = 0;
        var remainingTruncated = false;

        foreach (var action in job.Actions)
        {
            if (ct.IsCancellationRequested)
                return new StorageCleanupRoundResult { Cancelled = true };

            if (sliceWatch.Elapsed > sliceDeadline)
            {
                allComplete = false;
                continue;
            }

            try
            {
                var complete = action.Kind switch
                {
                    StorageCleanupKind.ClearField => await ExecuteClearFieldRoundAsync(job, action, sliceWatch, sliceDeadline, ct),
                    StorageCleanupKind.DeleteRows => await ExecuteDeleteRoundAsync(job, action, archive: false, sliceWatch, sliceDeadline, ct),
                    StorageCleanupKind.ArchiveAndDeleteRows => await ExecuteDeleteRoundAsync(job, action, archive: true, sliceWatch, sliceDeadline, ct),
                    StorageCleanupKind.DeleteLogFiles => await ExecuteLogFilesRoundAsync(job, action, ct),
                    StorageCleanupKind.DerivedHandler => await ExecuteDerivedRoundAsync(job, action, ct),
                    _ => true,
                };
                if (!complete)
                    allComplete = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                job.Warnings.Add($"目标 {action.TargetId} 本轮失败：{ex.Message}");
                _logger.LogWarning(
                    ex, "[StorageCleanup] action failed job={JobId} target={TargetId}", job.JobId, action.TargetId);
                allComplete = false;
            }

            // 剩余量有界探测（进度展示与 needs_confirmation 判定）；
            // 探测条件必须与动作语义一致：清字段模式只统计仍含大字段的行。
            if (action is { Table: not null, TimestampColumn: not null, Kind: not StorageCleanupKind.DerivedHandler })
            {
                var (remaining, truncated) = await ProbeRemainingAsync(action, job, CancellationToken.None);
                remainingEstimate += remaining;
                remainingTruncated |= truncated;
            }
        }

        var needsConfirmation = remainingEstimate > 0
            && job.ProcessedRows + remainingEstimate > job.Budget.MaxRowsPerJob;

        return new StorageCleanupRoundResult
        {
            AllActionsComplete = allComplete && remainingEstimate == 0,
            NeedsConfirmation = needsConfirmation,
            RemainingRowsEstimate = remainingEstimate,
            RemainingTruncated = remainingTruncated,
        };
    }

    private async Task<bool> ExecuteDeleteRoundAsync(
        StorageCleanupJob job,
        StorageCleanupAction action,
        bool archive,
        Stopwatch sliceWatch,
        TimeSpan sliceDeadline,
        CancellationToken ct)
    {
        var databasePath = ResolveDatabasePath(action);
        if (!File.Exists(databasePath))
            return true;

        await using var connection = await OpenReadWriteConnectionAsync(databasePath, ct);
        if (!await TableExistsAsync(connection, action.Table!, ct))
            return true;

        var cutoffString = job.CutoffUtc.ToString("O");
        var cursor = GetCursor(job, action, "0");
        var batches = 0;
        var busyRetries = 0;

        while (batches < job.Budget.MaxBatchesPerTargetPerRound && sliceWatch.Elapsed < sliceDeadline)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<Dictionary<string, object?>>? archivedRows = null;
            var rowIds = new List<long>(job.Budget.BatchSize);

            try
            {
                if (archive)
                {
                    archivedRows = await ReadBatchAsync(connection, action.Table!, action.TimestampColumn!, cutoffString, cursor, job.Budget.BatchSize, ct);
                    rowIds.AddRange(archivedRows.Select(r => Convert.ToInt64(r["__rowid"])));
                    if (archivedRows.Count > 0)
                        await _archiveWriter.ArchiveBatchAsync(action.Table!, archivedRows, job.CutoffUtc, ct);
                }
                else
                {
                    rowIds.AddRange(await SelectRowIdsAsync(
                        connection, action.Table!, action.TimestampColumn!, cutoffString, cursor, job.Budget.BatchSize, ct));
                }

                if (rowIds.Count == 0)
                {
                    SetCursor(job, action, "done");
                    return true;
                }

                var deleted = await DeleteByRowIdsAsync(connection, action.Table!, rowIds, ct);
                job.DeletedRows += deleted;
                AddProcessed(job, action.TargetId, deleted);
                cursor = rowIds[^1].ToString(System.Globalization.CultureInfo.InvariantCulture);
                SetCursor(job, action, cursor);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                busyRetries++;
                if (busyRetries > BusyRetryLimit)
                {
                    job.Warnings.Add($"目标 {action.TargetId} 遇到持续 SQLite busy，本轮让位稍后续行");
                    return false;
                }

                await Task.Delay(BusyBackoff, ct);
                continue;
            }

            batches++;
            if (batches >= job.Budget.MaxBatchesPerTargetPerRound)
                return false;

            if (job.Budget.BatchDelayMs > 0)
                await Task.Delay(job.Budget.BatchDelayMs, ct);
        }

        return false;
    }

    private async Task<bool> ExecuteClearFieldRoundAsync(
        StorageCleanupJob job,
        StorageCleanupAction action,
        Stopwatch sliceWatch,
        TimeSpan sliceDeadline,
        CancellationToken ct)
    {
        var databasePath = ResolveDatabasePath(action);
        if (!File.Exists(databasePath))
            return true;

        await using var connection = await OpenReadWriteConnectionAsync(databasePath, ct);
        if (!await TableExistsAsync(connection, action.Table!, ct))
            return true;

        var cutoffString = job.CutoffUtc.ToString("O");
        var cursor = GetCursor(job, action, "0");
        var columns = action.ClearColumns ?? [];
        if (columns.Length == 0)
            return true;

        var setExpression = string.Join(", ", columns.Select(c => $"{QuoteIdentifier(c)}=NULL"));
        var notNullExpression = string.Join(" OR ", columns.Select(c => $"{QuoteIdentifier(c)} IS NOT NULL"));
        var batches = 0;
        var busyRetries = 0;

        while (batches < job.Budget.MaxBatchesPerTargetPerRound && sliceWatch.Elapsed < sliceDeadline)
        {
            ct.ThrowIfCancellationRequested();

            List<long> rowIds;
            try
            {
                rowIds = await SelectClearableRowIdsAsync(
                    connection, action.Table!, action.TimestampColumn!, notNullExpression, cutoffString, cursor, job.Budget.BatchSize, ct);
                if (rowIds.Count == 0)
                {
                    SetCursor(job, action, "done");
                    return true;
                }

                var affected = await ClearFieldsByRowIdsAsync(connection, action.Table!, setExpression, rowIds, ct);
                job.ClearedRows += affected;
                AddProcessed(job, action.TargetId, affected);
                cursor = rowIds[^1].ToString(System.Globalization.CultureInfo.InvariantCulture);
                SetCursor(job, action, cursor);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                busyRetries++;
                if (busyRetries > BusyRetryLimit)
                {
                    job.Warnings.Add($"目标 {action.TargetId} 遇到持续 SQLite busy，本轮让位稍后续行");
                    return false;
                }

                await Task.Delay(BusyBackoff, ct);
                continue;
            }

            batches++;
            if (batches >= job.Budget.MaxBatchesPerTargetPerRound)
                return false;

            if (job.Budget.BatchDelayMs > 0)
                await Task.Delay(job.Budget.BatchDelayMs, ct);
        }

        return false;
    }

    private async Task<bool> ExecuteLogFilesRoundAsync(
        StorageCleanupJob job, StorageCleanupAction action, CancellationToken ct)
    {
        var cutoffUtc = job.CutoffUtc.UtcDateTime;
        var budget = job.Budget.MaxBatchesPerTargetPerRound * job.Budget.BatchSize;
        var deleted = 0;

        foreach (var relativeRoot in action.LogRoots ?? [])
        {
            var root = Path.GetFullPath(Path.Combine(_paths.DataRoot, relativeRoot));
            if (!root.StartsWith(_paths.DataRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
                continue;

            var pending = new Stack<string>([root]);
            while (pending.Count > 0 && deleted < budget)
            {
                ct.ThrowIfCancellationRequested();
                var current = pending.Pop();
                FileSystemInfo[] entries;
                try
                {
                    entries = new DirectoryInfo(current).GetFileSystemInfos();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (deleted >= budget)
                        break;
                    try
                    {
                        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry.FullName);
                            continue;
                        }

                        // Execute 前签名重校验：日志扩展名白名单 + 最后写入时间早于 cutoff。
                        if (entry is not FileInfo file
                            || !StorageInventorySampler.IsLogFileName(file.Name)
                            || file.LastWriteTimeUtc >= cutoffUtc)
                        {
                            continue;
                        }

                        var length = file.Length;
                        file.Delete();
                        deleted++;
                        job.DeletedFiles++;
                        AddProcessed(job, action.TargetId, 1);
                        job.ReusableBytesEstimate += length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        job.SkippedRows++;
                    }
                }
            }
        }

        // 日志清理一轮即完成（budget 内）；超出部分下轮继续。
        return deleted < budget;
    }

    private async Task<bool> ExecuteDerivedRoundAsync(
        StorageCleanupJob job, StorageCleanupAction action, CancellationToken ct)
    {
        var handler = _derivedHandlers.FirstOrDefault(h =>
            string.Equals(h.HandlerId, action.HandlerId, StringComparison.Ordinal));
        if (handler is null)
        {
            job.Warnings.Add($"目标 {action.TargetId} 的派生处理器不可用，已跳过");
            return true;
        }

        var result = await handler.ExecuteRoundAsync(job.CutoffUtc, ct);
        AddProcessed(job, action.TargetId, result.ProcessedCount);
        if (result.UnitCount > 0)
        {
            job.TargetUnits[action.TargetId] = job.TargetUnits.TryGetValue(action.TargetId, out var units)
                ? units + result.UnitCount
                : result.UnitCount;
        }
        job.Warnings.AddRange(result.Warnings);
        return result.Complete;
    }

    // ─── SQL helpers ──────────────────────────────────────────────

    private string ResolveDatabasePath(StorageCleanupAction action) =>
        Path.Combine(_paths.DatabasesRoot, action.DatabaseFile ?? StorageDataClassCatalog.PlatformDatabaseFile);

    private static async Task<List<long>> SelectRowIdsAsync(
        SqliteConnection connection, string table, string timestampColumn,
        string cutoffString, string cursor, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT rowid FROM {QuoteIdentifier(table)} " +
            $"WHERE {QuoteIdentifier(timestampColumn)} < $cutoff AND rowid > $cursor " +
            $"ORDER BY rowid LIMIT $limit";
        command.Parameters.AddWithValue("$cutoff", cutoffString);
        command.Parameters.AddWithValue("$cursor", long.TryParse(cursor, out var parsed) ? parsed : 0L);
        command.Parameters.AddWithValue("$limit", limit);
        command.CommandTimeout = 15;
        var ids = new List<long>(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task<List<long>> SelectClearableRowIdsAsync(
        SqliteConnection connection, string table, string timestampColumn,
        string notNullExpression, string cutoffString, string cursor, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT rowid FROM {QuoteIdentifier(table)} " +
            $"WHERE {QuoteIdentifier(timestampColumn)} < $cutoff AND rowid > $cursor AND ({notNullExpression}) " +
            $"ORDER BY rowid LIMIT $limit";
        command.Parameters.AddWithValue("$cutoff", cutoffString);
        command.Parameters.AddWithValue("$cursor", long.TryParse(cursor, out var parsed) ? parsed : 0L);
        command.Parameters.AddWithValue("$limit", limit);
        command.CommandTimeout = 15;
        var ids = new List<long>(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task<int> DeleteByRowIdsAsync(
        SqliteConnection connection, string table, IReadOnlyList<long> rowIds, CancellationToken ct)
    {
        var parameterNames = rowIds.Select((_, index) => $"$id{index}").ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DELETE FROM {QuoteIdentifier(table)} WHERE rowid IN ({string.Join(",", parameterNames)})";
        for (var i = 0; i < rowIds.Count; i++)
            command.Parameters.AddWithValue(parameterNames[i], rowIds[i]);
        command.CommandTimeout = 15;
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ClearFieldsByRowIdsAsync(
        SqliteConnection connection, string table, string setExpression, IReadOnlyList<long> rowIds, CancellationToken ct)
    {
        var parameterNames = rowIds.Select((_, index) => $"$id{index}").ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {QuoteIdentifier(table)} SET {setExpression} WHERE rowid IN ({string.Join(",", parameterNames)})";
        for (var i = 0; i < rowIds.Count; i++)
            command.Parameters.AddWithValue(parameterNames[i], rowIds[i]);
        command.CommandTimeout = 15;
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>读取整行（含 __rowid）用于归档；列名来自结果集元数据，非用户输入。</summary>
    private static async Task<List<Dictionary<string, object?>>> ReadBatchAsync(
        SqliteConnection connection, string table, string timestampColumn,
        string cutoffString, string cursor, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT rowid AS __rowid, * FROM {QuoteIdentifier(table)} " +
            $"WHERE {QuoteIdentifier(timestampColumn)} < $cutoff AND rowid > $cursor " +
            $"ORDER BY rowid LIMIT $limit";
        command.Parameters.AddWithValue("$cutoff", cutoffString);
        command.Parameters.AddWithValue("$cursor", long.TryParse(cursor, out var parsed) ? parsed : 0L);
        command.Parameters.AddWithValue("$limit", limit);
        command.CommandTimeout = 15;

        var rows = new List<Dictionary<string, object?>>(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        do
        {
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.IsDBNull(i))
                    {
                        row[reader.GetName(i)] = null;
                        continue;
                    }

                    row[reader.GetName(i)] = reader.GetFieldType(i) == typeof(string)
                        ? reader.GetString(i)
                        : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }
        while (await reader.NextResultAsync(ct));
        return rows;
    }

    internal static async Task<SqliteConnection> OpenReadWriteConnectionAsync(string databasePath, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct);
        // 维护 writer 短 busy timeout：立即让步，由执行器退避重试，不占住前台。
        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=2000";
        await busy.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        command.CommandTimeout = 5;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static string GetCursor(StorageCleanupJob job, StorageCleanupAction action, string fallback) =>
        job.Cursors.TryGetValue(action.TargetId, out var value) && value != "done" ? value : fallback;

    private static void AddProcessed(StorageCleanupJob job, string targetId, long delta)
    {
        if (delta <= 0)
            return;
        job.ProcessedRows += delta;
        job.DiscoveredRows += delta;
        job.TargetProcessed[targetId] = job.TargetProcessed.TryGetValue(targetId, out var existing)
            ? existing + delta
            : delta;
    }

    private static void SetCursor(StorageCleanupJob job, StorageCleanupAction action, string value) =>
        job.Cursors[action.TargetId] = value;

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
