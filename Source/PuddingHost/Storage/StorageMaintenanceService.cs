using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PuddingCode.Configuration;
using PuddingCode.Storage;
using PuddingCodeIntelligence.Contracts;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingHost.Storage;

/// <summary>
/// Core-owned SQLite and code-index maintenance. All destructive choices are
/// selected from a fixed semantic whitelist and require a short-lived preview.
/// Source files, chat transcripts, session facts, memory and configuration are
/// never deletion targets.
/// ADR-076: 写操作统一委托 StorageMaintenanceCoordinator（单 writer），
/// 在线 VACUUM 已下线——删除只转为 SQLite 可复用页。
/// </summary>
public sealed class StorageMaintenanceService(
    PuddingDataPaths dataPaths,
    ICodeIndexScheduler codeIndexScheduler,
    StorageMaintenanceCoordinator maintenanceCoordinator,
    ILogger<StorageMaintenanceService> logger) : IStorageMaintenanceService, IDisposable
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StaleCodeIndexThreshold = TimeSpan.FromHours(24);
    private const int DeleteBatchSize = 5_000;

    private static readonly string[] TelemetryTables =
    [
        "telemetry_metric_events",
        "context_layer_metric_events",
    ];

    private static readonly string[] RuntimeActivityTables = ["runtime_activity"];
    private static readonly string[] ProtectedEventTables =
    [
        "conversation_events",
    ];

    private static readonly string[] CodeIndexArtifactTables =
    [
        "CodeReferences",
        "CodeRelations",
        "CodeSymbols",
        "CodeFiles",
        "CodeIndexRuns",
    ];

    private static readonly string[] RedundantConversationIndexKeys =
    [
        "ix_ce_seq",
        "ix_ce_eid",
        "ix_ce_turn",
    ];

    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, PendingPreview> _previews = new();
    private int _disposeState;

    private string PlatformDatabasePath =>
        Path.Combine(dataPaths.DatabasesRoot, "pudding_platform.db");

    private string CodeIndexDatabasePath =>
        Path.Combine(dataPaths.DatabasesRoot, "code-index", "code_index.db");

    private string MemoryDatabasePath =>
        Path.Combine(dataPaths.DatabasesRoot, "pudding_memory.db");

    private string ControllerDatabasePath =>
        Path.Combine(dataPaths.DatabasesRoot, "pudding_controller.db");

    private string FullTextIndexRoot =>
        Path.Combine(dataPaths.DataRoot, "fulltext-index");

    public async Task<StorageDatabaseAnalysis> AnalyzeAsync(
        CancellationToken cancellationToken = default)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            return await AnalyzeCoreAsync(cancellationToken);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public async Task<StorageCleanupPreview> PreviewCleanupAsync(
        StorageCleanupPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetIds = NormalizeTargetIds(request.TargetIds);
        if (targetIds.Count == 0)
            throw new ArgumentException("At least one cleanup target is required.", nameof(request));
        if (request.RetentionDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "RetentionDays must be between 1 and 365.");

        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            RemoveExpiredPreviews();
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-request.RetentionDays);
            var warnings = new List<string>();
            var analysis = await AnalyzeCoreAsync(cancellationToken);
            var targets = new List<StorageCleanupTargetPreview>();
            var obsoleteScopes = Array.Empty<StorageMaintenanceQueries.CodeIndexScopeCandidate>();
            var redundantIndexes = Array.Empty<string>();

            foreach (var targetId in targetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (targetId)
                {
                    case StorageMaintenanceTargetIds.Telemetry:
                    {
                        var rows = await CountExpiredRowsAsync(
                            PlatformDatabasePath,
                            TelemetryTables.Select(table => (table, "occurred_at_utc")),
                            cutoff,
                            cancellationToken);
                        targets.Add(CreateTargetPreview(
                            analysis,
                            targetId,
                            "遥测与上下文指标",
                            rows,
                            $"删除 {request.RetentionDays} 天前的遥测与上下文指标；Token 汇总、聊天正文和记忆不受影响。"));
                        break;
                    }
                    case StorageMaintenanceTargetIds.RuntimeActivity:
                    {
                        var rows = await CountExpiredRowsAsync(
                            PlatformDatabasePath,
                            RuntimeActivityTables.Select(table => (table, "started_at_utc")),
                            cutoff,
                            cancellationToken);
                        targets.Add(CreateTargetPreview(
                            analysis,
                            targetId,
                            "运行活动明细",
                            rows,
                            $"删除 {request.RetentionDays} 天前的运行活动诊断明细，不删除会话消息或执行事实。"));
                        break;
                    }
                    case StorageMaintenanceTargetIds.DuplicateIndexes:
                    {
                        redundantIndexes = await FindRedundantConversationIndexesAsync(
                            PlatformDatabasePath,
                            cancellationToken);
                        targets.Add(CreateTargetPreview(
                            analysis,
                            targetId,
                            "重复或失效的数据库索引",
                            redundantIndexes.LongLength,
                            redundantIndexes.Length == 0
                                ? "没有发现已确认的重复或失效索引。"
                                : $"删除 {redundantIndexes.Length:N0} 个已确认重复，或只服务于已禁用裁剪路径的旧索引。"));
                        break;
                    }
                    case StorageMaintenanceTargetIds.ObsoleteCodeIndexScopes:
                    {
                        obsoleteScopes = await FindObsoleteCodeIndexScopesAsync(
                            CodeIndexDatabasePath,
                            now,
                            cancellationToken);
                        long rows = 0;
                        foreach (var scope in obsoleteScopes)
                        {
                            if (codeIndexScheduler.IsIndexing(scope.WorkspaceId, scope.ProjectId))
                            {
                                warnings.Add($"代码索引 {scope.DisplayName} 正在运行，预览已跳过。");
                                continue;
                            }
                            rows += scope.ArtifactRows + 1;
                        }
                        obsoleteScopes = obsoleteScopes
                            .Where(scope => !codeIndexScheduler.IsIndexing(scope.WorkspaceId, scope.ProjectId))
                            .ToArray();
                        targets.Add(CreateTargetPreview(
                            analysis,
                            targetId,
                            "冗余代码索引作用域",
                            rows,
                            obsoleteScopes.Length == 0
                                ? "没有发现已覆盖或失效超过 24 小时的代码索引作用域。"
                                : $"移除 {obsoleteScopes.Length:N0} 个已覆盖或失效的索引作用域及其派生数据；不会删除源代码。"));
                        break;
                    }
                }
            }

            var previewId = Guid.NewGuid();
            var preview = new StorageCleanupPreview
            {
                PreviewId = previewId,
                CreatedAt = now,
                ExpiresAt = now.Add(PreviewLifetime),
                RetentionDays = request.RetentionDays,
                CompactAfterCleanup = request.CompactAfterCleanup,
                Targets = targets,
                CandidateRows = targets.Sum(target => target.CandidateRows),
                EstimatedReclaimableBytes = SumNullable(
                    targets.Select(target => target.EstimatedReclaimableBytes)),
                Warnings = warnings,
            };

            _previews[previewId] = new PendingPreview(
                preview,
                cutoff,
                targetIds,
                obsoleteScopes,
                redundantIndexes);
            return preview;
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    /// <summary>
    /// ADR-076：Execute 不再直接写数据库。旧目标 ID 翻译为新语义集合后提交
    /// StorageMaintenanceCoordinator（单 writer、小批、busy 让步），并同步等待终态
    /// 以保持旧 /databases 端点的同步契约（Desktop 旧页面不改）。在线 VACUUM 已下线。
    /// </summary>
    public async Task<StorageCleanupResult> ExecuteCleanupAsync(
        Guid previewId,
        CancellationToken cancellationToken = default)
    {
        if (previewId == Guid.Empty)
            throw new ArgumentException("PreviewId is required.", nameof(previewId));

        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            RemoveExpiredPreviews();
            if (!_previews.TryRemove(previewId, out var pending))
                throw new InvalidOperationException("清理预览不存在或已过期，请重新生成预览。");
            if (pending.Preview.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("清理预览已过期，请重新生成预览。");

            // 旧目标 ID → 新语义目录 ID（过渡期翻译，ADR-076 §4.1）。
            var semanticTargets = pending.TargetIds
                .SelectMany(StorageDataClassCatalog.TranslateLegacyTarget)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var job = await maintenanceCoordinator.SubmitLegacyAsync(
                semanticTargets, pending.CutoffUtc, cancellationToken);

            // 同步等待终态：作业按 200 批/目标分轮执行，轮间让位人工作业；
            // 每 5 分钟检查一次（Desktop HttpClient 超时 6 分钟内必须返回）。
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                job = await maintenanceCoordinator.WaitForNextCheckpointAsync(
                    job.JobId, TimeSpan.FromMinutes(2), cancellationToken);
                if (job.Status is StorageCleanupJobStatus.Completed
                    or StorageCleanupJobStatus.Partial
                    or StorageCleanupJobStatus.Failed
                    or StorageCleanupJobStatus.Cancelled)
                {
                    break;
                }
            }

            var warnings = new List<string>(job.Warnings);
            if (pending.Preview.CompactAfterCleanup)
            {
                warnings.Add(
                    "在线数据库压缩（VACUUM）已按存储治理设计下线：本次清理的空间已转为 SQLite 库内可复用页，数据库文件不会立即缩小。");
            }

            var analysis = await AnalyzeCoreAsync(cancellationToken);
            var bytesAfter = GetDatabaseFilesTotalBytes();
            logger.LogInformation(
                "[StorageMaintenance] legacy preview={PreviewId} job={JobId} status={Status} " +
                "deletedRows={DeletedRows} clearedRows={ClearedRows}",
                previewId, job.JobId, job.Status, job.DeletedRows, job.ClearedRows);

            return new StorageCleanupResult
            {
                PreviewId = previewId,
                CompletedAt = DateTimeOffset.UtcNow,
                DeletedRows = job.DeletedRows + job.ClearedRows
                    + job.TargetProcessed.GetValueOrDefault(StorageAdminTargetIds.ObsoleteCodeIndexScopes),
                DroppedIndexes = (int)job.TargetUnits.GetValueOrDefault(StorageAdminTargetIds.RedundantIndexes),
                RemovedCodeIndexScopes = (int)job.TargetUnits.GetValueOrDefault(StorageAdminTargetIds.ObsoleteCodeIndexScopes),
                BytesBefore = bytesAfter,
                BytesAfter = bytesAfter,
                CompactedDatabases = [],
                Warnings = warnings,
                Analysis = analysis,
            };
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private async Task<StorageDatabaseAnalysis> AnalyzeCoreAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        var platform = await ReadDatabaseSnapshotAsync(
            "platform",
            "Pudding 平台数据库",
            PlatformDatabasePath,
            warnings,
            ct);
        var codeIndex = await ReadDatabaseSnapshotAsync(
            "code-index",
            "代码索引数据库",
            CodeIndexDatabasePath,
            warnings,
            ct);
        var memory = await ReadDatabaseSnapshotAsync(
            "memory",
            "记忆数据库",
            MemoryDatabasePath,
            warnings,
            ct);
        var controller = await ReadDatabaseSnapshotAsync(
            "controller",
            "控制面数据库",
            ControllerDatabasePath,
            warnings,
            ct);
        var fullTextIndex = ReadDirectorySnapshot(
            "fulltext-index",
            "全文检索索引",
            FullTextIndexRoot,
            warnings,
            ct);

        var platformObjectBytes = await TryReadObjectBytesAsync(
            PlatformDatabasePath,
            TelemetryTables
                .Concat(RuntimeActivityTables)
                .Concat(RedundantConversationIndexKeys)
                .ToArray(),
            warnings,
            ct);
        // A full dbstat scan over the code graph can traverse gigabytes and is
        // inappropriate for an interactive refresh. File/page totals and row
        // counts remain exact/near-exact; per-object bytes stay unavailable.
        Dictionary<string, long>? codeIndexObjectBytes = null;

        var protectedRows = await SumApproximateRowsAsync(
            PlatformDatabasePath,
            ProtectedEventTables,
            ct);
        var telemetryRows = await SumApproximateRowsAsync(
            PlatformDatabasePath,
            TelemetryTables,
            ct);
        var runtimeRows = await SumApproximateRowsAsync(
            PlatformDatabasePath,
            RuntimeActivityTables,
            ct);
        var duplicateIndexes = await FindRedundantConversationIndexesAsync(
            PlatformDatabasePath,
            ct);

        var now = DateTimeOffset.UtcNow;
        var obsoleteScopes = await FindObsoleteCodeIndexScopesAsync(
            CodeIndexDatabasePath,
            now,
            ct);
        var obsoleteRows = obsoleteScopes
            .Where(scope => !codeIndexScheduler.IsIndexing(scope.WorkspaceId, scope.ProjectId))
            .Sum(scope => scope.ArtifactRows + 1);
        var codeIndexRows = await SumApproximateRowsAsync(
            CodeIndexDatabasePath,
            CodeIndexArtifactTables,
            ct);
        var memoryRows = await SumAllUserTableApproximateRowsAsync(
            MemoryDatabasePath,
            ct);
        var controllerRows = await SumAllUserTableApproximateRowsAsync(
            ControllerDatabasePath,
            ct);

        var items = new List<StorageMaintenanceItemSnapshot>
        {
            new()
            {
                ItemId = "platform.execution-facts",
                DatabaseId = "platform",
                DisplayName = "会话与执行事实",
                Description = "conversation_events，是断线恢复、回放和执行审计的事实源。",
                RowCount = protectedRows,
                RowCountIsApproximate = true,
                AllocatedBytes = SumObjectBytes(platformObjectBytes, ProtectedEventTables),
                CanClean = false,
                IsProtected = true,
                ProtectionReason = "权威事件源；没有完整投影水位与归档前禁止清理。",
            },
            new()
            {
                ItemId = StorageMaintenanceTargetIds.Telemetry,
                DatabaseId = "platform",
                DisplayName = "遥测与上下文指标",
                Description = "性能、调用与上下文缓存诊断明细，可按保留期裁剪。",
                RowCount = telemetryRows,
                RowCountIsApproximate = true,
                AllocatedBytes = SumObjectBytes(platformObjectBytes, TelemetryTables),
                CanClean = true,
                IsProtected = false,
                DefaultRetentionDays = 14,
            },
            new()
            {
                ItemId = StorageMaintenanceTargetIds.RuntimeActivity,
                DatabaseId = "platform",
                DisplayName = "运行活动明细",
                Description = "组件与执行阶段的诊断活动流水，可按保留期裁剪。",
                RowCount = runtimeRows,
                RowCountIsApproximate = true,
                AllocatedBytes = SumObjectBytes(platformObjectBytes, RuntimeActivityTables),
                CanClean = true,
                IsProtected = false,
                DefaultRetentionDays = 14,
            },
            new()
            {
                ItemId = StorageMaintenanceTargetIds.DuplicateIndexes,
                DatabaseId = "platform",
                DisplayName = "重复或失效的数据库索引",
                Description = "旧运行时索引与 EF Core 索引完全重复，或只服务于已禁用的权威事件裁剪。",
                RowCount = duplicateIndexes.LongLength,
                AllocatedBytes = SumObjectBytes(platformObjectBytes, duplicateIndexes),
                CanClean = duplicateIndexes.Length > 0,
                IsProtected = false,
            },
            new()
            {
                ItemId = "code-index.active-data",
                DatabaseId = "code-index",
                DisplayName = "有效代码索引",
                Description = "活动作用域的文件、符号、关系与引用索引，可由源代码重建但不会被一键删除。",
                RowCount = Math.Max(0, codeIndexRows - obsoleteRows),
                RowCountIsApproximate = true,
                AllocatedBytes = SumObjectBytes(codeIndexObjectBytes, CodeIndexArtifactTables),
                CanClean = false,
                IsProtected = true,
                ProtectionReason = "仍被活动作用域使用。",
            },
            new()
            {
                ItemId = StorageMaintenanceTargetIds.ObsoleteCodeIndexScopes,
                DatabaseId = "code-index",
                DisplayName = "冗余代码索引作用域",
                Description = "已被父级覆盖、已移除或失效超过 24 小时的派生索引；源代码不会删除。",
                RowCount = obsoleteRows,
                AllocatedBytes = EstimateShare(
                    SumObjectBytes(codeIndexObjectBytes, CodeIndexArtifactTables),
                    obsoleteRows,
                    codeIndexRows),
                CanClean = obsoleteRows > 0,
                IsProtected = false,
            },
            new()
            {
                ItemId = "memory.persisted-data",
                DatabaseId = "memory",
                DisplayName = "记忆与向量数据",
                Description = "长期记忆、事实、偏好、会话向量和潜意识任务状态。",
                RowCount = memoryRows,
                RowCountIsApproximate = true,
                CanClean = false,
                IsProtected = true,
                ProtectionReason = "用户长期数据，必须通过记忆领域 API 管理。",
            },
            new()
            {
                ItemId = "controller.persisted-data",
                DatabaseId = "controller",
                DisplayName = "控制面状态",
                Description = "Workspace 路由、审计和控制面持久状态。",
                RowCount = controllerRows,
                RowCountIsApproximate = true,
                CanClean = false,
                IsProtected = true,
                ProtectionReason = "控制面事实，不属于缓存。",
            },
            new()
            {
                ItemId = "fulltext-index.active-data",
                DatabaseId = "fulltext-index",
                DisplayName = "全文检索索引",
                Description = "Lucene/Jieba 派生检索索引；本批次仅统计，不在运行中一键重建。",
                RowCount = 0,
                CanClean = false,
                IsProtected = true,
                ProtectionReason = "活动检索索引；后续应通过索引重建作业管理。",
            },
        };

        var databases = new[] { platform, codeIndex, memory, controller, fullTextIndex };
        return new StorageDatabaseAnalysis
        {
            CapturedAt = DateTimeOffset.Now,
            TotalBytes = databases.Sum(database => database.TotalBytes),
            Databases = databases,
            Items = items,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    private async Task<StorageDatabaseFileSnapshot> ReadDatabaseSnapshotAsync(
        string databaseId,
        string displayName,
        string databasePath,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        var mainBytes = GetFileLength(databasePath);
        var walBytes = GetFileLength(databasePath + "-wal");
        var shmBytes = GetFileLength(databasePath + "-shm");
        long pageSize = 0;
        long pageCount = 0;
        long freePages = 0;

        if (mainBytes > 0)
        {
            try
            {
                await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
                pageSize = await ExecuteScalarLongAsync(connection, "PRAGMA page_size", ct);
                pageCount = await ExecuteScalarLongAsync(connection, "PRAGMA page_count", ct);
                freePages = await ExecuteScalarLongAsync(connection, "PRAGMA freelist_count", ct);
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                warnings.Add($"无法读取 {displayName} 页面统计：{ex.Message}");
            }
        }

        return new StorageDatabaseFileSnapshot
        {
            DatabaseId = databaseId,
            DisplayName = displayName,
            RelativePath = Path.GetRelativePath(dataPaths.DataRoot, databasePath),
            MainBytes = mainBytes,
            WalBytes = walBytes,
            SharedMemoryBytes = shmBytes,
            PageSizeBytes = pageSize,
            PageCount = pageCount,
            FreePageCount = freePages,
        };
    }

    private StorageDatabaseFileSnapshot ReadDirectorySnapshot(
        string databaseId,
        string displayName,
        string rootPath,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        long bytes = 0;
        if (Directory.Exists(rootPath))
        {
            var pending = new Stack<string>();
            pending.Push(rootPath);
            while (pending.Count > 0)
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
                    warnings.Add($"无法读取 {displayName}：{ex.Message}");
                    continue;
                }

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        entry.Refresh();
                        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                            pending.Push(entry.FullName);
                        else if (entry is FileInfo file)
                            bytes += file.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"无法读取 {displayName} 项目：{ex.Message}");
                    }
                }
            }
        }

        return new StorageDatabaseFileSnapshot
        {
            DatabaseId = databaseId,
            DisplayName = displayName,
            RelativePath = Path.GetRelativePath(dataPaths.DataRoot, rootPath),
            MainBytes = bytes,
            WalBytes = 0,
            SharedMemoryBytes = 0,
            PageSizeBytes = 0,
            PageCount = 0,
            FreePageCount = 0,
        };
    }

    private static async Task<Dictionary<string, long>?> TryReadObjectBytesAsync(
        string databasePath,
        IReadOnlyList<string> objectNames,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath) || objectNames.Count == 0)
            return null;

        try
        {
            await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
            await using var command = connection.CreateCommand();
            var parameterNames = objectNames
                .Select((_, index) => $"$name{index}")
                .ToArray();
            command.CommandText =
                "SELECT name, COALESCE(SUM(pgsize), 0) FROM dbstat " +
                $"WHERE name IN ({string.Join(",", parameterNames)}) GROUP BY name";
            for (var i = 0; i < objectNames.Count; i++)
                command.Parameters.AddWithValue(parameterNames[i], objectNames[i]);
            command.CommandTimeout = 15;
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetString(0)] = reader.GetInt64(1);
            return result;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            warnings.Add("当前 SQLite 运行库未提供 dbstat，数据库文件大小准确，但表/索引大小仅显示行数。 ");
            return null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            warnings.Add($"读取数据库对象大小失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<long> SumApproximateRowsAsync(
        string databasePath,
        IEnumerable<string> tables,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return 0;

        await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
        long total = 0;
        foreach (var table in tables)
        {
            if (!await TableExistsAsync(connection, table, ct))
                continue;
            total += await ExecuteScalarLongAsync(
                connection,
                $"SELECT COALESCE(MAX(rowid), 0) FROM {QuoteIdentifier(table)}",
                ct);
        }
        return total;
    }

    private static async Task<long> SumAllUserTableApproximateRowsAsync(
        string databasePath,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return 0;

        await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type='table' AND name NOT LIKE 'sqlite_%'
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                tables.Add(reader.GetString(0));
        }

        long total = 0;
        foreach (var table in tables)
        {
            try
            {
                total += await ExecuteScalarLongAsync(
                    connection,
                    $"SELECT COALESCE(MAX(rowid), 0) FROM {QuoteIdentifier(table)}",
                    ct);
            }
            catch (SqliteException)
            {
                // WITHOUT ROWID tables are uncommon here. File/page totals still
                // remain accurate and this count is explicitly approximate.
            }
        }
        return total;
    }

    private static async Task<long> CountExpiredRowsAsync(
        string databasePath,
        IEnumerable<(string Table, string TimestampColumn)> specs,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return 0;

        await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
        long total = 0;
        foreach (var (table, timestampColumn) in specs)
        {
            if (!await TableExistsAsync(connection, table, ct))
                continue;
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(*) FROM {QuoteIdentifier(table)} " +
                $"WHERE {QuoteIdentifier(timestampColumn)} < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            command.CommandTimeout = 60;
            total += Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        return total;
    }

    private static Task<StorageMaintenanceQueries.CodeIndexScopeCandidate[]> FindObsoleteCodeIndexScopesAsync(
        string databasePath, DateTimeOffset now, CancellationToken ct)
        => StorageMaintenanceQueries.FindObsoleteCodeIndexScopesAsync(databasePath, now, ct);

    private static Task<string[]> FindRedundantConversationIndexesAsync(
        string databasePath, CancellationToken ct)
        => StorageMaintenanceQueries.FindRedundantIndexesAsync(databasePath, ct);

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        bool readOnly,
        CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct);
        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=15000";
        await busy.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static IReadOnlyList<string> NormalizeTargetIds(IReadOnlyList<string>? targetIds)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            StorageMaintenanceTargetIds.Telemetry,
            StorageMaintenanceTargetIds.RuntimeActivity,
            StorageMaintenanceTargetIds.DuplicateIndexes,
            StorageMaintenanceTargetIds.ObsoleteCodeIndexScopes,
        };
        var normalized = (targetIds ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unknown = normalized.Where(target => !supported.Contains(target)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException($"Unsupported cleanup target: {string.Join(", ", unknown)}");
        return normalized;
    }

    private static StorageCleanupTargetPreview CreateTargetPreview(
        StorageDatabaseAnalysis analysis,
        string targetId,
        string displayName,
        long candidateRows,
        string summary)
    {
        var item = analysis.Items.FirstOrDefault(value => value.ItemId == targetId);
        return new StorageCleanupTargetPreview
        {
            TargetId = targetId,
            DisplayName = displayName,
            CandidateRows = candidateRows,
            EstimatedReclaimableBytes = EstimateShare(
                item?.AllocatedBytes,
                candidateRows,
                item?.RowCount ?? 0),
            Summary = summary,
        };
    }

    private static long? SumObjectBytes(
        IReadOnlyDictionary<string, long>? objectBytes,
        IEnumerable<string> objectNames)
    {
        if (objectBytes is null)
            return null;
        long total = 0;
        foreach (var name in objectNames)
        {
            if (objectBytes.TryGetValue(name, out var bytes))
                total += bytes;
        }
        return total;
    }

    private static long? EstimateShare(long? bytes, long numerator, long denominator)
    {
        if (bytes is null || numerator <= 0 || denominator <= 0)
            return null;
        return (long)Math.Min(bytes.Value, bytes.Value * (double)numerator / denominator);
    }

    private static long? SumNullable(IEnumerable<long?> values)
    {
        var materialized = values.ToArray();
        return materialized.Any(value => value.HasValue)
            ? materialized.Sum(value => value ?? 0)
            : null;
    }

    private long GetDatabaseFilesTotalBytes()
        => GetDatabaseTotalBytes(PlatformDatabasePath)
           + GetDatabaseTotalBytes(CodeIndexDatabasePath);

    private static long GetDatabaseTotalBytes(string databasePath)
        => GetFileLength(databasePath)
           + GetFileLength(databasePath + "-wal")
           + GetFileLength(databasePath + "-shm");

    private static long GetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string GetDatabaseDisplayName(string databasePath)
        => databasePath.EndsWith("code_index.db", StringComparison.OrdinalIgnoreCase)
            ? "代码索引数据库"
            : "Pudding 平台数据库";

    private void RemoveExpiredPreviews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _previews)
        {
            if (pair.Value.Preview.ExpiresAt <= now)
                _previews.TryRemove(pair.Key, out _);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        _maintenanceLock.Dispose();
    }

    private sealed record PendingPreview(
        StorageCleanupPreview Preview,
        DateTimeOffset CutoffUtc,
        IReadOnlyList<string> TargetIds,
        IReadOnlyList<StorageMaintenanceQueries.CodeIndexScopeCandidate> ObsoleteScopes,
        IReadOnlyList<string> RedundantIndexes);


}
