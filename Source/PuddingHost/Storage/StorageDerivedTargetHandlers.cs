using Microsoft.Data.Sqlite;
using PuddingCode.Configuration;
using PuddingCode.Storage;
using PuddingCodeIntelligence.Contracts;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingHost.Storage;

/// <summary>
/// 冗余代码索引作用域派生目标处理器（目录 HandlerId=code-index-scopes）。
/// 查找逻辑与旧 /databases 分析器共享（StorageMaintenanceQueries）；
/// 删除在协调器串行队列内执行，执行前重校验作用域状态。
/// </summary>
public sealed class CodeIndexScopeCleanupHandler(
    PuddingDataPaths dataPaths,
    ICodeIndexScheduler codeIndexScheduler,
    ILogger<CodeIndexScopeCleanupHandler> logger) : IStorageDerivedTargetHandler
{
    public string HandlerId => "code-index-scopes";

    private string CodeIndexDatabasePath =>
        Path.Combine(dataPaths.DatabasesRoot, "code-index", "code_index.db");

    public async Task<StorageDerivedEstimate> EstimateAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var scopes = await StorageMaintenanceQueries.FindObsoleteCodeIndexScopesAsync(
            CodeIndexDatabasePath, DateTimeOffset.UtcNow, ct);
        var candidates = scopes
            .Where(scope => !codeIndexScheduler.IsIndexing(scope.WorkspaceId, scope.ProjectId))
            .ToList();
        return new StorageDerivedEstimate
        {
            CandidateCount = candidates.Sum(scope => scope.ArtifactRows + 1),
            PreviewItems = [.. candidates.Take(20).Select(scope => scope.DisplayName)],
            Warning = candidates.Count == 0 ? "没有发现已覆盖或失效超过 24 小时的代码索引作用域。" : null,
        };
    }

    public async Task<StorageDerivedExecution> ExecuteRoundAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var warnings = new List<string>();
        var scopes = await StorageMaintenanceQueries.FindObsoleteCodeIndexScopesAsync(
            CodeIndexDatabasePath, DateTimeOffset.UtcNow, ct);
        long processed = 0;

        foreach (var scope in scopes)
        {
            if (ct.IsCancellationRequested)
                break;
            if (codeIndexScheduler.IsIndexing(scope.WorkspaceId, scope.ProjectId))
            {
                warnings.Add($"代码索引 {scope.DisplayName} 正在运行，已跳过。");
                continue;
            }

            var removed = await RemoveScopeAsync(scope, ct);
            if (!removed)
            {
                warnings.Add($"代码索引 {scope.DisplayName} 的状态已变化，已跳过。");
                continue;
            }

            processed += scope.ArtifactRows + 1;
        }

        logger.LogInformation("[CodeIndexCleanup] removed scopes rows={Rows}", processed);
        return new StorageDerivedExecution
        {
            ProcessedCount = processed,
            UnitCount = scopes.Length,
            Complete = !ct.IsCancellationRequested,
            Warnings = warnings,
        };
    }

    private async Task<bool> RemoveScopeAsync(
        StorageMaintenanceQueries.CodeIndexScopeCandidate candidate, CancellationToken ct)
    {
        if (!File.Exists(CodeIndexDatabasePath))
            return false;

        await using var connection = await StorageMaintenanceQueries.OpenConnectionAsync(
            CodeIndexDatabasePath, readOnly: false, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // 执行前重校验：预览后状态可能已变化。
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
            DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(24)).ToString("O"));
        if (Convert.ToInt64(await recheck.ExecuteScalarAsync(ct)) != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        foreach (var table in StorageMaintenanceQueries.CodeIndexArtifactTables)
        {
            if (!await StorageMaintenanceQueries.TableExistsAsync(connection, table, ct, (SqliteTransaction)transaction))
                continue;
            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText =
                $"DELETE FROM {StorageMaintenanceQueries.QuoteIdentifier(table)} " +
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
}

/// <summary>
/// 冗余 SQLite 索引派生目标处理器（目录 HandlerId=redundant-indexes）。
/// 只删除与 EF Core 正式索引定义完全重复的旧运行时索引；语义目录声明的
/// retention 索引（依赖索引）永不进入删除集合。
/// </summary>
public sealed class RedundantIndexCleanupHandler(
    PuddingDataPaths dataPaths,
    ILogger<RedundantIndexCleanupHandler> logger) : IStorageDerivedTargetHandler
{
    public string HandlerId => "redundant-indexes";

    private string PlatformDatabasePath =>
        Path.Combine(dataPaths.DatabasesRoot, "pudding_platform.db");

    public async Task<StorageDerivedEstimate> EstimateAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var indexes = await StorageMaintenanceQueries.FindRedundantIndexesAsync(PlatformDatabasePath, ct);
        return new StorageDerivedEstimate
        {
            CandidateCount = indexes.Length,
            PreviewItems = indexes,
            Warning = indexes.Length == 0 ? "没有发现已确认的重复或失效索引。" : null,
        };
    }

    public async Task<StorageDerivedExecution> ExecuteRoundAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var warnings = new List<string>();
        var protectedIndexes = new HashSet<string>(
            StorageDataClassCatalog.Definitions.SelectMany(d => d.RetentionIndexes),
            StringComparer.OrdinalIgnoreCase);

        // 执行前重新校验定义，只删除预览时已确认且仍冗余、且不属于依赖索引的项。
        var currentlyRedundant = await StorageMaintenanceQueries.FindRedundantIndexesAsync(PlatformDatabasePath, ct);
        var allowed = currentlyRedundant
            .Where(name => !protectedIndexes.Contains(name))
            .ToList();

        long dropped = 0;
        if (allowed.Count > 0)
        {
            await using var connection = await StorageMaintenanceQueries.OpenConnectionAsync(
                PlatformDatabasePath, readOnly: false, ct);
            foreach (var indexName in allowed)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP INDEX IF EXISTS {StorageMaintenanceQueries.QuoteIdentifier(indexName)}";
                await command.ExecuteNonQueryAsync(ct);
                dropped++;
            }
        }

        logger.LogInformation("[RedundantIndexCleanup] dropped={Dropped}", dropped);
        return new StorageDerivedExecution
        {
            ProcessedCount = dropped,
            UnitCount = dropped,
            Complete = true,
            Warnings = warnings,
        };
    }
}
