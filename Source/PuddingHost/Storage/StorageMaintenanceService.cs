using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PuddingCode.Configuration;
using PuddingCode.Storage;
using PuddingCodeIntelligence.Contracts;

namespace PuddingHost.Storage;

/// <summary>
/// Core-owned SQLite and code-index maintenance. All destructive choices are
/// selected from a fixed semantic whitelist and require a short-lived preview.
/// Source files, chat transcripts, session facts, memory and configuration are
/// never deletion targets.
/// </summary>
public sealed class StorageMaintenanceService(
    PuddingDataPaths dataPaths,
    ICodeIndexScheduler codeIndexScheduler,
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
        "session_event_log",
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

    private static readonly IReadOnlyDictionary<string, string> RedundantConversationIndexes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ix_ce_seq"] = "IX_conversation_events_conversation_id_sequence",
            ["ix_ce_eid"] = "IX_conversation_events_event_id",
            ["ix_ce_turn"] = "IX_conversation_events_turn_id_type",
        };

    private const string ObsoleteConversationRetentionIndex =
        "ix_retention_conversation_events_committed_at";

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
            var obsoleteScopes = Array.Empty<CodeIndexScopeCandidate>();
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

            var warnings = new List<string>();
            var compacted = new List<string>();
            var modifiedDatabases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bytesBefore = GetDatabaseFilesTotalBytes();
            long deletedRows = 0;
            var droppedIndexes = 0;
            var removedScopes = 0;

            foreach (var targetId in pending.TargetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (targetId)
                {
                    case StorageMaintenanceTargetIds.Telemetry:
                        deletedRows += await DeleteExpiredRowsAsync(
                            PlatformDatabasePath,
                            TelemetryTables.Select(table => (table, "occurred_at_utc")),
                            pending.CutoffUtc,
                            cancellationToken);
                        modifiedDatabases.Add(PlatformDatabasePath);
                        break;
                    case StorageMaintenanceTargetIds.RuntimeActivity:
                        deletedRows += await DeleteExpiredRowsAsync(
                            PlatformDatabasePath,
                            RuntimeActivityTables.Select(table => (table, "started_at_utc")),
                            pending.CutoffUtc,
                            cancellationToken);
                        modifiedDatabases.Add(PlatformDatabasePath);
                        break;
                    case StorageMaintenanceTargetIds.DuplicateIndexes:
                        droppedIndexes += await DropRedundantIndexesAsync(
                            PlatformDatabasePath,
                            pending.RedundantIndexes,
                            cancellationToken);
                        if (pending.RedundantIndexes.Count > 0)
                            modifiedDatabases.Add(PlatformDatabasePath);
                        break;
                    case StorageMaintenanceTargetIds.ObsoleteCodeIndexScopes:
                    {
                        foreach (var scope in pending.ObsoleteScopes)
                        {
                            if (codeIndexScheduler.IsIndexing(scope.WorkspaceId, scope.ProjectId))
                            {
                                warnings.Add($"代码索引 {scope.DisplayName} 在确认后开始运行，已跳过。");
                                continue;
                            }

                            var removed = await RemoveObsoleteCodeIndexScopeAsync(
                                CodeIndexDatabasePath,
                                scope,
                                cancellationToken);
                            if (!removed)
                            {
                                warnings.Add($"代码索引 {scope.DisplayName} 的状态已变化，已跳过。");
                                continue;
                            }

                            deletedRows += scope.ArtifactRows + 1;
                            removedScopes++;
                        }
                        if (removedScopes > 0)
                            modifiedDatabases.Add(CodeIndexDatabasePath);
                        break;
                    }
                }
            }

            if (pending.Preview.CompactAfterCleanup)
            {
                foreach (var databasePath in modifiedDatabases)
                {
                    var displayName = GetDatabaseDisplayName(databasePath);
                    if (await TryCompactDatabaseAsync(databasePath, warnings, cancellationToken))
                        compacted.Add(displayName);
                }
            }

            var analysis = await AnalyzeCoreAsync(cancellationToken);
            var bytesAfter = GetDatabaseFilesTotalBytes();
            logger.LogInformation(
                "[StorageMaintenance] preview={PreviewId} deletedRows={DeletedRows} " +
                "droppedIndexes={DroppedIndexes} removedScopes={RemovedScopes} " +
                "bytesBefore={BytesBefore} bytesAfter={BytesAfter} compacted={Compacted}",
                previewId,
                deletedRows,
                droppedIndexes,
                removedScopes,
                bytesBefore,
                bytesAfter,
                compacted);

            return new StorageCleanupResult
            {
                PreviewId = previewId,
                CompletedAt = DateTimeOffset.UtcNow,
                DeletedRows = deletedRows,
                DroppedIndexes = droppedIndexes,
                RemovedCodeIndexScopes = removedScopes,
                BytesBefore = bytesBefore,
                BytesAfter = bytesAfter,
                CompactedDatabases = compacted,
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
                .Concat(RedundantConversationIndexes.Keys)
                .Append(ObsoleteConversationRetentionIndex)
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
                Description = "session_event_log 与 conversation_events，是断线恢复、回放和执行审计的事实源。",
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

    private static async Task<long> DeleteExpiredRowsAsync(
        string databasePath,
        IEnumerable<(string Table, string TimestampColumn)> specs,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return 0;

        long total = 0;
        await using var connection = await OpenConnectionAsync(databasePath, readOnly: false, ct);
        foreach (var (table, timestampColumn) in specs)
        {
            if (!await TableExistsAsync(connection, table, ct))
                continue;
            while (true)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $$"""
                    DELETE FROM {{QuoteIdentifier(table)}}
                    WHERE rowid IN (
                        SELECT rowid FROM {{QuoteIdentifier(table)}}
                        WHERE {{QuoteIdentifier(timestampColumn)}} < $cutoff
                        LIMIT $batchSize
                    )
                    """;
                command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
                command.Parameters.AddWithValue("$batchSize", DeleteBatchSize);
                command.CommandTimeout = 60;
                var affected = await command.ExecuteNonQueryAsync(ct);
                total += affected;
                if (affected < DeleteBatchSize)
                    break;
                await Task.Yield();
            }
        }
        return total;
    }

    private async Task<CodeIndexScopeCandidate[]> FindObsoleteCodeIndexScopesAsync(
        string databasePath,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return [];

        await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
        if (!await TableExistsAsync(connection, "CodeProjects", ct))
            return [];

        var staleBefore = now.Subtract(StaleCodeIndexThreshold).ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkspaceId, ProjectId, COALESCE(DisplayName, ProjectPath, ProjectId)
            FROM CodeProjects
            WHERE ScopeState IN ('Covered', 'Removed')
               OR (
                    ScopeState IS NULL
                    AND Status IN ('Removed', 'Failed', 'Registering')
                    AND COALESCE(UpdatedAtUtc, AddedAtUtc, '') < $staleBefore
               )
            ORDER BY WorkspaceId, ProjectId
            """;
        command.Parameters.AddWithValue("$staleBefore", staleBefore);
        var candidates = new List<CodeIndexScopeCandidate>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                candidates.Add(new CodeIndexScopeCandidate(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ArtifactRows: 0));
            }
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            long rows = 0;
            foreach (var table in CodeIndexArtifactTables)
            {
                if (!await TableExistsAsync(connection, table, ct))
                    continue;
                await using var countCommand = connection.CreateCommand();
                countCommand.CommandText =
                    $"SELECT COUNT(*) FROM {QuoteIdentifier(table)} " +
                    "WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId";
                countCommand.Parameters.AddWithValue("$workspaceId", candidate.WorkspaceId);
                countCommand.Parameters.AddWithValue("$projectId", candidate.ProjectId);
                countCommand.CommandTimeout = 60;
                rows += Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct));
            }
            candidates[i] = candidate with { ArtifactRows = rows };
        }

        return candidates.ToArray();
    }

    private static async Task<bool> RemoveObsoleteCodeIndexScopeAsync(
        string databasePath,
        CodeIndexScopeCandidate candidate,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return false;

        await using var connection = await OpenConnectionAsync(databasePath, readOnly: false, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var recheck = connection.CreateCommand();
        recheck.Transaction = (SqliteTransaction)transaction;
        recheck.CommandText = """
            SELECT COUNT(*)
            FROM CodeProjects
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
              AND (
                    ScopeState IN ('Covered', 'Removed')
                    OR (
                         ScopeState IS NULL
                         AND Status IN ('Removed', 'Failed', 'Registering')
                         AND COALESCE(UpdatedAtUtc, AddedAtUtc, '') < $staleBefore
                       )
                  )
            """;
        recheck.Parameters.AddWithValue("$workspaceId", candidate.WorkspaceId);
        recheck.Parameters.AddWithValue("$projectId", candidate.ProjectId);
        recheck.Parameters.AddWithValue(
            "$staleBefore",
            DateTimeOffset.UtcNow.Subtract(StaleCodeIndexThreshold).ToString("O"));
        if (Convert.ToInt64(await recheck.ExecuteScalarAsync(ct)) != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        foreach (var table in CodeIndexArtifactTables)
        {
            if (!await TableExistsAsync(connection, table, ct, (SqliteTransaction)transaction))
                continue;
            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText =
                $"DELETE FROM {QuoteIdentifier(table)} " +
                "WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId";
            delete.Parameters.AddWithValue("$workspaceId", candidate.WorkspaceId);
            delete.Parameters.AddWithValue("$projectId", candidate.ProjectId);
            delete.CommandTimeout = 120;
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using var deleteProject = connection.CreateCommand();
        deleteProject.Transaction = (SqliteTransaction)transaction;
        deleteProject.CommandText = """
            DELETE FROM CodeProjects
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
            """;
        deleteProject.Parameters.AddWithValue("$workspaceId", candidate.WorkspaceId);
        deleteProject.Parameters.AddWithValue("$projectId", candidate.ProjectId);
        await deleteProject.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task<string[]> FindRedundantConversationIndexesAsync(
        string databasePath,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return [];

        await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
        if (!await TableExistsAsync(connection, "conversation_events", ct))
            return [];

        var indexDefinitions = await ReadIndexDefinitionsAsync(connection, "conversation_events", ct);
        var redundant = new List<string>();
        foreach (var (legacyName, canonicalName) in RedundantConversationIndexes)
        {
            if (!indexDefinitions.TryGetValue(legacyName, out var legacy)
                || !indexDefinitions.TryGetValue(canonicalName, out var canonical))
            {
                continue;
            }
            if (legacy.Unique == canonical.Unique
                && legacy.Columns.SequenceEqual(canonical.Columns, StringComparer.OrdinalIgnoreCase))
            {
                redundant.Add(legacyName);
            }
        }
        if (indexDefinitions.TryGetValue(ObsoleteConversationRetentionIndex, out var retention)
            && !retention.Unique
            && retention.Columns.SequenceEqual(["committed_at"], StringComparer.OrdinalIgnoreCase))
        {
            redundant.Add(ObsoleteConversationRetentionIndex);
        }
        return redundant.ToArray();
    }

    private static async Task<int> DropRedundantIndexesAsync(
        string databasePath,
        IReadOnlyList<string> previewedIndexes,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath) || previewedIndexes.Count == 0)
            return 0;

        var currentlyRedundant = await FindRedundantConversationIndexesAsync(databasePath, ct);
        var allowed = previewedIndexes
            .Intersect(currentlyRedundant, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowed.Length == 0)
            return 0;

        await using var connection = await OpenConnectionAsync(databasePath, readOnly: false, ct);
        var dropped = 0;
        foreach (var indexName in allowed)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP INDEX IF EXISTS {QuoteIdentifier(indexName)}";
            await command.ExecuteNonQueryAsync(ct);
            dropped++;
        }
        return dropped;
    }

    private static async Task<Dictionary<string, IndexDefinition>> ReadIndexDefinitionsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var definitions = new Dictionary<string, IndexDefinition>(StringComparer.OrdinalIgnoreCase);
        var indexRows = new List<(string Name, bool Unique)>();
        await using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)})";
            await using var reader = await listCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                indexRows.Add((reader.GetString(1), reader.GetInt64(2) != 0));
        }

        foreach (var (name, unique) in indexRows)
        {
            var columns = new List<string>();
            await using var infoCommand = connection.CreateCommand();
            infoCommand.CommandText = $"PRAGMA index_info({QuoteIdentifier(name)})";
            await using var infoReader = await infoCommand.ExecuteReaderAsync(ct);
            while (await infoReader.ReadAsync(ct))
                columns.Add(infoReader.GetString(2));
            definitions[name] = new IndexDefinition(unique, columns.ToArray());
        }
        return definitions;
    }

    private static async Task<bool> TryCompactDatabaseAsync(
        string databasePath,
        ICollection<string> warnings,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return false;

        var databaseBytes = GetFileLength(databasePath);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(databasePath));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                var required = databaseBytes + 64L * 1024 * 1024;
                if (drive.IsReady && drive.AvailableFreeSpace < required)
                {
                    warnings.Add(
                        $"{Path.GetFileName(databasePath)} 已完成行清理，但可用空间不足以安全压缩数据库文件。");
                    return false;
                }
            }

            await using var connection = await OpenConnectionAsync(databasePath, readOnly: false, ct);
            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.CommandTimeout = 60;
                await checkpoint.ExecuteNonQueryAsync(ct);
            }
            await using (var vacuum = connection.CreateCommand())
            {
                vacuum.CommandText = "VACUUM";
                vacuum.CommandTimeout = 300;
                await vacuum.ExecuteNonQueryAsync(ct);
            }
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            warnings.Add(
                $"{Path.GetFileName(databasePath)} 已完成行清理，但压缩失败：{ex.Message}");
            return false;
        }
    }

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
        IReadOnlyList<CodeIndexScopeCandidate> ObsoleteScopes,
        IReadOnlyList<string> RedundantIndexes);

    private sealed record CodeIndexScopeCandidate(
        string WorkspaceId,
        string ProjectId,
        string DisplayName,
        long ArtifactRows);

    private sealed record IndexDefinition(bool Unique, IReadOnlyList<string> Columns);
}
