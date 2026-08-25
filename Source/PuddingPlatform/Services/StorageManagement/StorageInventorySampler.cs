using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// ADR-076 §5.1 增量空间清单采样器。全部查询/枚举都由索引、LIMIT 或文件数结构性有界，
/// 绝不把一次性全量扫描搬进后台线程；每个 slice 预算 100ms、slice 间让步。
/// “刷新估算”只提交 refresh request（重复请求合并），立即返回，页面继续使用旧快照。
/// </summary>
public sealed class StorageInventorySampler : BackgroundService
{
    private const int SliceBudgetMs = 100;
    private const int SliceGapDelayMs = 50;
    private const int RowSampleLimit = 300;
    private const int MaxFilesPerLogRoot = 2_000;
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    private readonly PuddingDataPaths _paths;
    private readonly StorageInventorySnapshotStore _store;
    private readonly IEnumerable<IStorageDerivedTargetHandler> _derivedHandlers;
    private readonly ILogger<StorageInventorySampler> _logger;
    private readonly object _refreshGate = new();

    private StorageInventoryRefreshStatusDto? _activeRefresh;
    private StorageInventoryRefreshStatusDto? _lastRefresh;
    private int _refreshRunning;

    public StorageInventorySampler(
        PuddingDataPaths paths,
        StorageInventorySnapshotStore store,
        IEnumerable<IStorageDerivedTargetHandler> derivedHandlers,
        ILogger<StorageInventorySampler> logger)
    {
        _paths = paths;
        _store = store;
        _derivedHandlers = derivedHandlers;
        _logger = logger;
    }

    /// <summary>提交异步刷新请求：已在刷新则合并返回同一 refreshId（202 语义），否则后台启动。</summary>
    public Task<StorageInventoryRefreshStatusDto> RequestRefreshAsync(CancellationToken ct = default)
    {
        lock (_refreshGate)
        {
            if (_activeRefresh is { } running)
                return Task.FromResult(running);

            var status = new StorageInventoryRefreshStatusDto
            {
                RefreshId = Guid.NewGuid(),
                State = StorageInventoryRefreshState.Running,
                RequestedAtUtc = DateTimeOffset.UtcNow,
                SnapshotRevision = _store.Current.Revision,
            };
            _activeRefresh = status;
            _ = RunRefreshCycleAsync(status.RefreshId, ct);
            return Task.FromResult(status);
        }
    }

    public StorageInventoryRefreshStatusDto? GetRefreshStatus(Guid refreshId)
    {
        lock (_refreshGate)
        {
            if (_activeRefresh is { } running && running.RefreshId == refreshId)
                return running;
            if (_lastRefresh is { } last && last.RefreshId == refreshId)
                return last;
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 绝不阻塞宿主启动；Ready 信号不受采样影响。
        await Task.Yield();
        try
        {
            await _store.LoadAsync(stoppingToken);
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                lock (_refreshGate)
                {
                    // 手动刷新进行中时跳过本轮定时刷新（请求合并）。
                    if (_activeRefresh is null)
                    {
                        var status = new StorageInventoryRefreshStatusDto
                        {
                            RefreshId = Guid.NewGuid(),
                            State = StorageInventoryRefreshState.Running,
                            RequestedAtUtc = DateTimeOffset.UtcNow,
                            SnapshotRevision = _store.Current.Revision,
                        };
                        _activeRefresh = status;
                        _ = RunRefreshCycleAsync(status.RefreshId, stoppingToken);
                    }
                }

                await Task.Delay(DefaultRefreshInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StorageInventory] sampler loop failed");
        }
    }

    private async Task RunRefreshCycleAsync(Guid refreshId, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _refreshRunning, 1) != 0)
        {
            // 上一轮仍在收尾：本次请求已合并到活动刷新，直接返回其状态。
            return;
        }

        var warnings = new List<string>();
        try
        {
            _store.MergeSnapshot(databases: null, classes: null, isRefreshing: true);

            foreach (var slice in BuildSlices())
            {
                if (ct.IsCancellationRequested)
                    break;

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var (databases, classes) = await slice.ExecuteAsync(ct);
                    _store.MergeSnapshot(databases, classes, isRefreshing: true, warnings);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"分类 {slice.TargetId} 本次采样失败，保留上次估算：{ex.Message}");
                    _store.MergeSnapshot(
                        databases: null,
                        classes: [UnavailableClass(slice.TargetId)],
                        isRefreshing: true,
                        warnings);
                }

                if (stopwatch.ElapsedMilliseconds > SliceBudgetMs * 5)
                {
                    warnings.Add($"分类 {slice.TargetId} 采样耗时 {stopwatch.ElapsedMilliseconds}ms 超出预期预算");
                }

                // slice 间让步：前台请求/写入优先。
                await Task.Delay(SliceGapDelayMs, ct);
            }

            _store.MergeSnapshot(databases: null, classes: null, isRefreshing: false, warnings: warnings.Distinct(StringComparer.Ordinal).ToList());
            await _store.TryAppendHistoryPointAsync(ct);

            lock (_refreshGate)
            {
                _lastRefresh = new StorageInventoryRefreshStatusDto
                {
                    RefreshId = refreshId,
                    State = StorageInventoryRefreshState.Completed,
                    RequestedAtUtc = _activeRefresh?.RequestedAtUtc ?? DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    SnapshotRevision = _store.Current.Revision,
                };
                _activeRefresh = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StorageInventory] refresh cycle failed refreshId={RefreshId}", refreshId);
            warnings.Add($"刷新失败：{ex.Message}");
            _store.MergeSnapshot(databases: null, classes: null, isRefreshing: false, warnings);

            lock (_refreshGate)
            {
                _lastRefresh = new StorageInventoryRefreshStatusDto
                {
                    RefreshId = refreshId,
                    State = StorageInventoryRefreshState.Failed,
                    RequestedAtUtc = _activeRefresh?.RequestedAtUtc ?? DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    SnapshotRevision = _store.Current.Revision,
                };
                _activeRefresh = null;
            }
        }
        finally
        {
            Volatile.Write(ref _refreshRunning, 0);
        }
    }

    private static StorageInventoryClassDto UnavailableClass(string targetId) => new()
    {
        TargetId = targetId,
        DisplayName = StorageDataClassCatalog.Find(targetId)?.DisplayName ?? targetId,
        EstimateState = StorageEstimateState.Unavailable,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    // ─── Slice 构建 ───────────────────────────────────────────────

    private IEnumerable<InventorySlice> BuildSlices()
    {
        yield return new InventorySlice("databases.files", ct => Task.Run(() =>
        {
            var databases = ReadDatabaseFileSnapshots();
            return Task.FromResult<(IReadOnlyList<StorageInventoryDatabaseDto>?, IReadOnlyList<StorageInventoryClassDto>?)>((databases, null));
        }, ct));

        foreach (var definition in StorageDataClassCatalog.Definitions)
        {
            var target = definition;
            if (target.RequiresDerivedHandler)
            {
                var handler = _derivedHandlers.FirstOrDefault(h =>
                    string.Equals(h.HandlerId, target.HandlerId, StringComparison.Ordinal));
                if (handler is null)
                    continue;

                yield return new InventorySlice(target.TargetId, async ct =>
                {
                    var estimate = await handler.EstimateAsync(DateTimeOffset.UtcNow, ct);
                    return (null, new[]
                    {
                        new StorageInventoryClassDto
                        {
                            TargetId = target.TargetId,
                            DisplayName = target.DisplayName,
                            EstimatedBytes = null,
                            EstimatedRows = estimate.CandidateCount,
                            EstimateState = StorageEstimateState.Updated,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                        },
                    });
                });
                continue;
            }

            if (target.Tables.Count > 0)
            {
                yield return new InventorySlice(target.TargetId, ct =>
                    SampleTableClassAsync(target, ct));
            }

            if (target.LogRoots.Count > 0)
            {
                yield return new InventorySlice(target.TargetId, ct =>
                    Task.FromResult<(IReadOnlyList<StorageInventoryDatabaseDto>?, IReadOnlyList<StorageInventoryClassDto>?)>(
                        (null, [SampleLogClass(target)])));
            }
        }
    }

    private sealed record InventorySlice(
        string TargetId,
        Func<CancellationToken, Task<(IReadOnlyList<StorageInventoryDatabaseDto>? Databases, IReadOnlyList<StorageInventoryClassDto>? Classes)>> ExecuteAsync);

    // ─── 数据库文件元数据（廉价、精确）─────────────────────────────

    private IReadOnlyList<StorageInventoryDatabaseDto> ReadDatabaseFileSnapshots()
    {
        var snapshots = new List<StorageInventoryDatabaseDto>();
        snapshots.Add(ReadDatabaseSnapshot(
            "platform", "Pudding 平台数据库", Path.Combine(_paths.DatabasesRoot, StorageDataClassCatalog.PlatformDatabaseFile)));
        snapshots.Add(ReadDatabaseSnapshot(
            "code-index", "代码索引数据库", Path.Combine(_paths.DatabasesRoot, StorageDataClassCatalog.CodeIndexDatabaseFile)));
        snapshots.Add(ReadDatabaseSnapshot(
            "memory", "记忆数据库", Path.Combine(_paths.DatabasesRoot, "pudding_memory.db")));
        snapshots.Add(ReadDatabaseSnapshot(
            "controller", "控制面数据库", Path.Combine(_paths.DatabasesRoot, "pudding_controller.db")));
        snapshots.Add(ReadDirectorySnapshot(
            "fulltext-index", "全文检索索引", Path.Combine(_paths.DataRoot, "fulltext-index")));
        return snapshots;
    }

    private StorageInventoryDatabaseDto ReadDatabaseSnapshot(string databaseId, string displayName, string databasePath)
    {
        long pageSize = 0, pageCount = 0, freePages = 0;
        if (File.Exists(databasePath))
        {
            try
            {
                using var connection = OpenReadOnlyConnection(databasePath);
                pageSize = ExecuteScalarLong(connection, "PRAGMA page_size");
                pageCount = ExecuteScalarLong(connection, "PRAGMA page_count");
                freePages = ExecuteScalarLong(connection, "PRAGMA freelist_count");
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                // 页面统计缺失时文件字节数仍然精确。
            }
        }

        return new StorageInventoryDatabaseDto
        {
            DatabaseId = databaseId,
            DisplayName = displayName,
            RelativePath = Path.GetRelativePath(_paths.DataRoot, databasePath),
            MainBytes = GetFileLength(databasePath),
            WalBytes = GetFileLength(databasePath + "-wal"),
            SharedMemoryBytes = GetFileLength(databasePath + "-shm"),
            PageSizeBytes = pageSize,
            PageCount = pageCount,
            FreePageCount = freePages,
        };
    }

    private StorageInventoryDatabaseDto ReadDirectorySnapshot(string databaseId, string displayName, string rootPath)
    {
        long bytes = 0;
        if (Directory.Exists(rootPath))
        {
            var pending = new Stack<string>([rootPath]);
            var visited = 0;
            while (pending.Count > 0 && visited < MaxFilesPerLogRoot)
            {
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
                    if (visited >= MaxFilesPerLogRoot)
                        break;
                    try
                    {
                        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                            pending.Push(entry.FullName);
                        else if (entry is FileInfo file)
                        {
                            bytes += file.Length;
                            visited++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单项失败跳过。
                    }
                }
            }
        }

        return new StorageInventoryDatabaseDto
        {
            DatabaseId = databaseId,
            DisplayName = displayName,
            RelativePath = Path.GetRelativePath(_paths.DataRoot, rootPath),
            MainBytes = bytes,
            WalBytes = 0,
            SharedMemoryBytes = 0,
            PageSizeBytes = 0,
            PageCount = 0,
            FreePageCount = 0,
        };
    }

    // ─── 表分类估算（索引 + LIMIT 结构性有界）─────────────────────

    private async Task<(IReadOnlyList<StorageInventoryDatabaseDto>?, IReadOnlyList<StorageInventoryClassDto>?)> SampleTableClassAsync(
        StorageDataClassCatalog.StorageDataClassDefinition definition, CancellationToken ct)
    {
        var databasePath = Path.Combine(_paths.DatabasesRoot, definition.DatabaseFile ?? StorageDataClassCatalog.PlatformDatabaseFile);
        if (!File.Exists(databasePath))
        {
            return (null, [new StorageInventoryClassDto
            {
                TargetId = definition.TargetId,
                DisplayName = definition.DisplayName,
                EstimateState = StorageEstimateState.Unavailable,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            }]);
        }

        long totalRows = 0;
        long? totalBytes = null;
        DateTimeOffset? oldest = null, newest = null;

        await using var connection = OpenReadOnlyConnection(databasePath);

        foreach (var mapping in definition.Tables)
        {
            if (!await TableExistsAsync(connection, mapping.Table, ct))
                continue;

            // 时间范围：仅当该表存在以时间列为首列的索引时读取（索引首尾 O(log n)）；无索引时跳过，避免全表扫。
            var hasTimeIndex = await HasIndexOnColumnAsync(connection, mapping.Table, mapping.TimestampColumn, ct);

            // 行数：rowid B-tree 右端探测，O(log n)。
            var rows = await ExecuteScalarLongAsync(
                connection,
                $"SELECT COALESCE(MAX(rowid), 0) FROM {QuoteIdentifier(mapping.Table)}",
                CommandTimeoutSeconds: 5,
                ct);
            totalRows += rows;

            // 时间范围：仅当 retention 索引存在时读取（索引首尾 O(log n)）；无索引时跳过，避免全表扫。
            if (hasTimeIndex)
            {
                var (minTs, maxTs) = await ReadTimeRangeAsync(connection, mapping.Table, mapping.TimestampColumn, ct);
                if (TryParseTimestamp(minTs) is { } min && (oldest is null || min < oldest))
                    oldest = min;
                if (TryParseTimestamp(maxTs) is { } max && (newest is null || max > newest))
                    newest = max;
            }

            // 均长：LIMIT 样本（前 N 行顺序读，结构性有界）。列清单来自 PRAGMA table_info（非用户输入）。
            var avgBytes = await EstimateAverageRowBytesAsync(connection, mapping.Table, mapping.ClearColumns, ct);
            if (avgBytes is { } avg)
            {
                var estimated = (long)(avg * rows);
                totalBytes = (totalBytes ?? 0) + estimated;
            }
        }

        return (null, [new StorageInventoryClassDto
        {
            TargetId = definition.TargetId,
            DisplayName = definition.DisplayName,
            EstimatedBytes = totalBytes,
            EstimatedRows = totalRows,
            OldestUtc = oldest,
            NewestUtc = newest,
            EstimateState = totalRows > 0 || totalBytes is not null
                ? StorageEstimateState.Updated
                : StorageEstimateState.Unavailable,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }]);
    }

    /// <summary>LIMIT 样本平均载荷字节。ClearColumns 非空时只估大字段列；否则估全部文本列。</summary>
    private static async Task<double?> EstimateAverageRowBytesAsync(
        SqliteConnection connection,
        string table,
        string[]? clearColumns,
        CancellationToken ct)
    {
        var columns = clearColumns;
        if (columns is null or { Length: 0 })
        {
            columns = await ReadTextColumnsAsync(connection, table, ct);
            if (columns.Length == 0)
                return null;
        }

        var lengthExpression = string.Join("+", columns.Select(c => $"COALESCE(length({QuoteIdentifier(c)}),0)"));
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COALESCE(SUM(len),0), COUNT(*) FROM (" +
            $"SELECT {lengthExpression} AS len FROM {QuoteIdentifier(table)} LIMIT {RowSampleLimit})";
        command.CommandTimeout = 5;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var total = reader.GetInt64(0);
        var count = reader.GetInt64(1);
        return count > 0 ? (double)total / count : null;
    }

    private static async Task<string[]> ReadTextColumnsAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
        command.CommandTimeout = 5;
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
            if (type.Contains("BLOB", StringComparison.OrdinalIgnoreCase))
                continue;
            columns.Add(name);
        }

        return [.. columns];
    }

    private static async Task<(string? Min, string? Max)> ReadTimeRangeAsync(
        SqliteConnection connection, string table, string timestampColumn, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT MIN({QuoteIdentifier(timestampColumn)}), MAX({QuoteIdentifier(timestampColumn)}) " +
            $"FROM {QuoteIdentifier(table)}";
        command.CommandTimeout = 5;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParseExact(value, "O", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out parsed))
            return parsed;
        if (DateOnly.TryParse(value, out var day))
            return new DateTimeOffset(day, TimeOnly.MinValue, TimeSpan.Zero);
        return null;
    }

    // ─── 日志分类（文件数结构性有界）───────────────────────────────

    private StorageInventoryClassDto SampleLogClass(StorageDataClassCatalog.StorageDataClassDefinition definition)
    {
        long bytes = 0, files = 0;
        DateTimeOffset? oldest = null;

        foreach (var relativeRoot in definition.LogRoots)
        {
            var root = Path.GetFullPath(Path.Combine(_paths.DataRoot, relativeRoot));
            if (!root.StartsWith(_paths.DataRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
                continue;

            var pending = new Stack<string>([root]);
            while (pending.Count > 0 && files < MaxFilesPerLogRoot)
            {
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
                    if (files >= MaxFilesPerLogRoot)
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

                        if (entry is not FileInfo file || !IsLogFileName(file.Name))
                            continue;
                        bytes += file.Length;
                        files++;
                        var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                        if (oldest is null || lastWrite < oldest)
                            oldest = lastWrite;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单项失败跳过。
                    }
                }
            }
        }

        return new StorageInventoryClassDto
        {
            TargetId = definition.TargetId,
            DisplayName = definition.DisplayName,
            EstimatedBytes = bytes,
            EstimatedRows = files,
            OldestUtc = oldest,
            EstimateState = files > 0 ? StorageEstimateState.Updated : StorageEstimateState.Unavailable,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    internal static bool IsLogFileName(string fileName) =>
        fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    // ─── SQLite helpers（短超时、只读连接）─────────────────────────

    private static SqliteConnection OpenReadOnlyConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=2000";
        busy.ExecuteNonQuery();
        return connection;
    }

    private static long ExecuteScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 5;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection, string sql, int CommandTimeoutSeconds, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        command.CommandTimeout = 5;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string table, string indexName, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name=$table AND name=$name";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$name", indexName);
        command.CommandTimeout = 5;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    /// <summary>该表是否存在以指定列为首列的索引（PRAGMA 驱动，列名非用户输入）。</summary>
    private static async Task<bool> HasIndexOnColumnAsync(
        SqliteConnection connection, string table, string column, CancellationToken ct)
    {
        var indexNames = new List<string>();
        await using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText = $"PRAGMA index_list({QuoteIdentifier(table)})";
            listCommand.CommandTimeout = 5;
            await using var reader = await listCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                indexNames.Add(reader.GetString(1));
        }

        foreach (var indexName in indexNames)
        {
            await using var infoCommand = connection.CreateCommand();
            infoCommand.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)})";
            infoCommand.CommandTimeout = 5;
            await using var infoReader = await infoCommand.ExecuteReaderAsync(ct);
            if (await infoReader.ReadAsync(ct)
                && infoReader.GetString(2).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long GetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
