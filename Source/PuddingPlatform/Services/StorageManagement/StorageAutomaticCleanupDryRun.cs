using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>单个候选数据类的试运行结果。</summary>
public readonly record struct StorageAutomaticCleanupDryRunEntry(
    string TargetId,
    int RetentionDays,
    DateTimeOffset CutoffUtc,
    long Candidates,
    bool Truncated);

/// <summary>
/// 一轮自动清理试运行的完整结果。
/// <see cref="SkippedByReason"/> 的键是 <see cref="StorageAutomaticCleanupPolicy.SkipReason"/> 的名称。
/// </summary>
public sealed record StorageAutomaticCleanupDryRunResult(
    int Evaluated,
    IReadOnlyList<StorageAutomaticCleanupDryRunEntry> Entries,
    IReadOnlyDictionary<string, int> SkippedByReason);

/// <summary>
/// 自动清理的**试运行**（dry-run）评估：算候选、数候选行数、写日志，仅此而已。
///
/// 为什么单独成类（2026-10-27）：
/// 1. **边界显式** —— 本类型不持有 <c>StorageMaintenanceJobStore</c>，也没有任何作业类型可用，
///    所以「试运行不会创建作业」不是靠断言保证，而是**由构造保证**（依赖不进来就做不到）。
/// 2. **可测** —— 只需要 <see cref="PuddingDataPaths"/> 与一个 logger，不必拉起
///    作业存储 / 清理执行器 / 归档写入器整条链，因此这一步能被单测直接驱动。
///
/// 行为约定：不建作业、不删数据、不改配置；探测失败只告警不中断。
/// </summary>
/// <remarks>
/// 公开可见是必要的：公开的 <c>StorageMaintenanceCoordinator</c> 构造函数要接收本类型作为参数
/// （C# 不允许公开成员暴露 internal 类型，CS0051）。
/// </remarks>
public sealed class StorageAutomaticCleanupDryRun
{
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<StorageAutomaticCleanupDryRun> _logger;

    public StorageAutomaticCleanupDryRun(
        PuddingDataPaths paths,
        ILogger<StorageAutomaticCleanupDryRun> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// 执行一轮试运行。参数 <paramref name="nowUtc"/> 显式传入而不是内部读时钟，
    /// 便于测试固定 cutoff 断言。
    /// <paramref name="definitions"/> 缺省用生产目录；测试可传入构造的目录以获得确定性断言，
    /// 而不必依赖真实目录的内容（真实目录会随新增数据类而变）。
    /// </summary>
    public async Task<StorageAutomaticCleanupDryRunResult> ExecuteAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken,
        IReadOnlyList<StorageDataClassCatalog.StorageDataClassDefinition>? definitions = null)
    {
        var source = definitions ?? StorageDataClassCatalog.Definitions;
        var entries = new List<StorageAutomaticCleanupDryRunEntry>();
        var skippedByReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var evaluated = 0;

        try
        {
            var decisions = StorageAutomaticCleanupPolicy.Evaluate(source, nowUtc);
            evaluated = decisions.Count;

            // 物理映射必须与决策用同一个来源：否则传入自定义目录时，决策命中、
            // 但 Find 去全局目录里找不到目标，会静默产出 0 条目。
            var definitionsById = new Dictionary<string, StorageDataClassCatalog.StorageDataClassDefinition>(StringComparer.Ordinal);
            foreach (var definition in source)
            {
                if (definition is not null)
                    definitionsById[definition.TargetId] = definition;
            }

            // 跳过原因无论有没有候选都要统计：零候选时它就是全部信息。
            foreach (var decision in decisions.Where(decision => !decision.IsCandidate))
            {
                var key = decision.Reason.ToString();
                skippedByReason.TryGetValue(key, out var existing);
                skippedByReason[key] = existing + 1;
            }

            var candidates = decisions.Where(decision => decision.IsCandidate).ToList();

            if (candidates.Count == 0)
            {
                _logger.LogInformation(
                    "[StorageMaintenance:Auto] dry-run evaluated={Evaluated} candidates=0",
                    decisions.Count);
            }
            else
            {
                foreach (var decision in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var candidate = decision.Candidate!.Value;
                    if (!definitionsById.TryGetValue(candidate.TargetId, out var definition))
                        continue;

                    long rows = 0;
                    var truncated = false;
                    try
                    {
                        if (definition.Tables.Count > 0)
                        {
                            var databasePath = Path.Combine(
                                _paths.DatabasesRoot,
                                definition.DatabaseFile ?? StorageDataClassCatalog.PlatformDatabaseFile);
                            if (File.Exists(databasePath))
                            {
                                await using var connection =
                                    await StorageCleanupExecutor.OpenReadWriteConnectionAsync(databasePath, cancellationToken);
                                foreach (var mapping in definition.Tables)
                                {
                                    var (count, wasTruncated) = await StorageCleanupExecutor.ProbeCandidatesAsync(
                                        connection,
                                        mapping.Table,
                                        mapping.TimestampColumn,
                                        candidate.CutoffUtc.ToString("O"),
                                        cancellationToken);
                                    rows += count;
                                    truncated |= wasTruncated;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[StorageMaintenance:Auto] probe failed target={TargetId}", candidate.TargetId);
                        continue;
                    }

                    entries.Add(new StorageAutomaticCleanupDryRunEntry(
                        candidate.TargetId,
                        candidate.RetentionDays,
                        candidate.CutoffUtc,
                        rows,
                        truncated));

                    _logger.LogInformation(
                        "[StorageMaintenance:Auto] dry-run target={TargetId} retentionDays={RetentionDays} cutoff={CutoffUtc:O} candidates={Candidates} truncated={Truncated} jobCreated=False",
                        candidate.TargetId,
                        candidate.RetentionDays,
                        candidate.CutoffUtc,
                        rows,
                        truncated);
                }

                _logger.LogInformation(
                    "[StorageMaintenance:Auto] dry-run summary evaluated={Evaluated} candidates={Candidates} skipped=[{Skipped}]",
                    decisions.Count,
                    candidates.Count,
                    string.Join(",", skippedByReason.Select(pair => $"{pair.Key}={pair.Value}")));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StorageMaintenance:Auto] evaluation crashed");
        }

        return new StorageAutomaticCleanupDryRunResult(evaluated, entries, skippedByReason);
    }
}
