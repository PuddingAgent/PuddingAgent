using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// ADR-076 §5.3 唯一维护写协调器：自动调度、Web 人工清理与旧 /databases 端点的全部删除
/// 都必须经此串行执行（单 writer），人工作业优先于自动作业，取消只发生在批次边界。
/// DataRoot 级 OS 独占文件锁防止误启的第二个 Core 并行维护。
/// </summary>
public sealed class StorageMaintenanceCoordinator : BackgroundService
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 自动清理评估周期（S1 第二部分，2026-10-27）。
    /// 在此之前 automatic 队列只有重启恢复会写入，没有任何周期性生产者 —— 自动清理从未触发。
    /// 评估成本是每张表的候选计数探测，不宜频繁；默认 6 小时。
    /// </summary>
    private static readonly TimeSpan AutomaticEvaluationInterval = TimeSpan.FromHours(6);

    private readonly StorageMaintenanceJobStore _jobStore;
    private readonly StorageCleanupExecutor _executor;
    private readonly IEnumerable<IStorageDerivedTargetHandler> _derivedHandlers;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<StorageMaintenanceCoordinator> _logger;

    private readonly Channel<Guid> _manualQueue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });
    private readonly Channel<Guid> _automaticQueue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<StorageCleanupJob>> _completions = new();
    private readonly ConcurrentDictionary<Guid, PendingAdminPreview> _previews = new();
    private readonly ConcurrentDictionary<string, Guid> _requestIdIndex = new(StringComparer.Ordinal);

    private FileStream? _maintenanceLock;

    public StorageMaintenanceCoordinator(
        StorageMaintenanceJobStore jobStore,
        StorageCleanupExecutor executor,
        IEnumerable<IStorageDerivedTargetHandler> derivedHandlers,
        PuddingDataPaths paths,
        ILogger<StorageMaintenanceCoordinator> logger)
    {
        _jobStore = jobStore;
        _executor = executor;
        _derivedHandlers = derivedHandlers;
        _paths = paths;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _jobStore.LoadAsync(cancellationToken);
        await _jobStore.PruneAsync(cancellationToken);

        // DataRoot 级独占锁：第二个 Core/维护进程无法并行写（崩溃后由 OS 自动释放）。
        try
        {
            var lockDir = Path.Combine(_paths.DataRoot, "maintenance", "storage");
            Directory.CreateDirectory(lockDir);
            _maintenanceLock = new FileStream(
                Path.Combine(lockDir, "maintenance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[StorageMaintenance] DataRoot maintenance lock held by another process — cleanup disabled");
            return;
        }

        // 恢复的 queued 作业重新入队（从已提交 cursor 续行）。
        foreach (var job in _jobStore.ListRecent(200))
        {
            if (job.Status == StorageCleanupJobStatus.Queued)
            {
                var channel = job.Trigger == "automatic" ? _automaticQueue : _manualQueue;
                await channel.Writer.WriteAsync(job.JobId, cancellationToken);
            }
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var cancellation in _cancellations.Values)
        {
            cancellation.Cancel();
        }

        _manualQueue.Writer.TryComplete();
        _automaticQueue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        _maintenanceLock?.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 人工作业优先于到期自动作业。
                if (_manualQueue.Reader.TryRead(out var jobId)
                    || _automaticQueue.Reader.TryRead(out jobId))
                {
                    try
                    {
                        await RunJobAsync(jobId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[StorageMaintenance] job={JobId} crashed", jobId);
                    }

                    continue;
                }

                // 双队列等待：任一有数据即唤醒。writer Complete 后 WaitToReadAsync
                // 同步返回 false，必须退出循环——否则会以同步 false 无限自旋，
                // ExecuteTask 永不完成，宿主 StopAsync 挂死。
                var manualRead = _manualQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var autoRead = _automaticQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                // 第三个唤醒源：周期评估。没有它，自动清理就永远没有生产者（S1 第二部分）。
                var evaluationTick = Task.Delay(AutomaticEvaluationInterval, stoppingToken);
                var finished = await Task.WhenAny(manualRead, autoRead, evaluationTick);
                if (finished == evaluationTick)
                {
                    await EvaluateAutomaticAsync(stoppingToken);
                    continue;
                }

                // 只有队列读任务带 bool 结果；Task.Delay 是非泛型 Task，不能直接取反。
                if (finished is Task<bool> queueRead && !await queueRead)
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 周期评估（S1 第二部分，2026-10-27）：按目录保留策略判断哪些数据类应当自动清理，
    /// 并以**试运行**方式把保留天数、cutoff 与候选行数写进日志。
    ///
    /// 本步只记录、不建作业、不删数据：自动删除的第一步必须先是「在日志里说清楚要删什么」，
    /// 看几轮之后再接 trigger=automatic 的作业生成。
    /// 之前 automatic 队列只有重启恢复会写入，所以自动清理从未触发过。
    /// </summary>
    private async Task EvaluateAutomaticAsync(CancellationToken stoppingToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var decisions = StorageAutomaticCleanupPolicy.Evaluate(StorageDataClassCatalog.Definitions, now);
            var candidates = decisions.Where(decision => decision.IsCandidate).ToList();

            if (candidates.Count == 0)
            {
                _logger.LogInformation(
                    "[StorageMaintenance:Auto] dry-run evaluated={Evaluated} candidates=0",
                    decisions.Count);
                return;
            }

            foreach (var decision in candidates)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var candidate = decision.Candidate!.Value;
                var definition = StorageDataClassCatalog.Find(candidate.TargetId);
                if (definition is null)
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
                                await StorageCleanupExecutor.OpenReadWriteConnectionAsync(databasePath, stoppingToken);
                            foreach (var mapping in definition.Tables)
                            {
                                var (count, wasTruncated) = await StorageCleanupExecutor.ProbeCandidatesAsync(
                                    connection, mapping.Table, mapping.TimestampColumn, candidate.CutoffUtc.ToString("O"), stoppingToken);
                                rows += count;
                                truncated |= wasTruncated;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StorageMaintenance:Auto] probe failed target={TargetId}", candidate.TargetId);
                    continue;
                }

                _logger.LogInformation(
                    "[StorageMaintenance:Auto] dry-run target={TargetId} retentionDays={RetentionDays} cutoff={CutoffUtc:O} candidates={Candidates} truncated={Truncated} jobCreated=False",
                    candidate.TargetId, candidate.RetentionDays, candidate.CutoffUtc, rows, truncated);
            }

            var skipped = string.Join(",", decisions
                .Where(decision => !decision.IsCandidate)
                .GroupBy(decision => decision.Reason)
                .Select(group => $"{group.Key}={group.Count()}"));
            _logger.LogInformation(
                "[StorageMaintenance:Auto] dry-run summary evaluated={Evaluated} candidates={Candidates} skipped=[{Skipped}]",
                decisions.Count, candidates.Count, skipped);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StorageMaintenance:Auto] evaluation crashed");
        }
    }

    private async Task RunJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        var job = _jobStore.Get(jobId);
        if (job is null || IsTerminal(job.Status))
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _cancellations[jobId] = cts;
        var completion = _completions.GetOrAdd(
            jobId, _ => new TaskCompletionSource<StorageCleanupJob>(TaskCreationOptions.RunContinuationsAsynchronously));

        job.Status = StorageCleanupJobStatus.Running;
        job.StartedAtUtc ??= DateTimeOffset.UtcNow;
        await _jobStore.PersistAsync(job);
        await _jobStore.AppendEventAsync(jobId, "storage.maintenance.started", counters: new Dictionary<string, long>
        {
            ["targets"] = job.TargetIds.Count,
        });

        try
        {
            while (true)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    job.Status = StorageCleanupJobStatus.Cancelled;
                    job.FinishedAtUtc = DateTimeOffset.UtcNow;
                    break;
                }

                var result = await _executor.ExecuteRoundAsync(job, cts.Token);

                if (result.NeedsConfirmation)
                {
                    if (job.Trigger == "automatic")
                    {
                        // 自动作业不等待人工确认：标记 partial，下个到期周期从 cursor 续行。
                        job.Status = StorageCleanupJobStatus.Partial;
                        job.Warnings.Add("已达单作业预算上限，剩余部分将在下个自动清理周期续行。");
                        job.FinishedAtUtc = DateTimeOffset.UtcNow;
                        await _jobStore.PersistAsync(job);
                        await _jobStore.AppendEventAsync(jobId, "storage.maintenance.partial");
                        completion.TrySetResult(job);
                        _cancellations.TryRemove(jobId, out _);
                        return;
                    }

                    job.Status = StorageCleanupJobStatus.NeedsConfirmation;
                    await _jobStore.PersistAsync(job);
                    await _jobStore.AppendEventAsync(
                        jobId, "storage.maintenance.needs_confirmation",
                        counters: new Dictionary<string, long> { ["processed"] = job.ProcessedRows });
                    completion.TrySetResult(job);
                    _cancellations.TryRemove(jobId, out _);
                    return;
                }

                await _jobStore.PersistAsync(job);
                await _jobStore.AppendEventAsync(jobId, "storage.maintenance.batch_completed", counters: new Dictionary<string, long>
                {
                    ["processed"] = job.ProcessedRows,
                    ["deleted"] = job.DeletedRows,
                    ["cleared"] = job.ClearedRows,
                    ["remaining"] = result.RemainingRowsEstimate,
                });

                if (result.AllActionsComplete)
                {
                    job.Status = job.FailedRows > 0 || job.Warnings.Count > 0
                        ? StorageCleanupJobStatus.Partial
                        : StorageCleanupJobStatus.Completed;
                    job.FinishedAtUtc = DateTimeOffset.UtcNow;
                    break;
                }

                // 未完成：写检查点后重新排队（自动让位于等待中的人工作业）。
                var channel = job.Trigger == "automatic" ? _automaticQueue : _manualQueue;
                await channel.Writer.WriteAsync(jobId, stoppingToken);
                _cancellations.TryRemove(jobId, out _);
                completion.TrySetResult(job);
                return;
            }

            await _jobStore.PersistAsync(job);
            await _jobStore.AppendEventAsync(jobId, $"storage.maintenance.{job.Status.ToString().ToLowerInvariant()}");
            completion.TrySetResult(job);
        }
        catch (OperationCanceledException)
        {
            job.Status = StorageCleanupJobStatus.Cancelled;
            job.FinishedAtUtc = DateTimeOffset.UtcNow;
            await _jobStore.PersistAsync(job);
            await _jobStore.AppendEventAsync(jobId, "storage.maintenance.cancelled");
            completion.TrySetResult(job);
        }
        catch (Exception ex)
        {
            job.Status = StorageCleanupJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.FinishedAtUtc = DateTimeOffset.UtcNow;
            await _jobStore.PersistAsync(job);
            await _jobStore.AppendEventAsync(jobId, "storage.maintenance.failed", message: ex.Message);
            completion.TrySetResult(job);
            _logger.LogError(ex, "[StorageMaintenance] job={JobId} failed", jobId);
        }
        finally
        {
            _cancellations.TryRemove(jobId, out _);
        }
    }

    private static bool IsTerminal(StorageCleanupJobStatus status) =>
        status is StorageCleanupJobStatus.Completed
            or StorageCleanupJobStatus.Partial
            or StorageCleanupJobStatus.Failed
            or StorageCleanupJobStatus.Cancelled;

    // ─── Preview（新语义 API）────────────────────────────────────

    public async Task<StorageCleanupPreviewDto> CreatePreviewAsync(
        StorageCleanupPreviewRequestDto request,
        int policyRevision,
        StorageInventorySnapshotDto snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetIds = (request.TargetIds ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (targetIds.Count == 0)
            throw new ArgumentException("At least one cleanup target is required.");

        var unknown = targetIds.Where(t => StorageDataClassCatalog.Find(t) is null).ToList();
        if (unknown.Count > 0)
            throw new StorageAdminException(
                StorageAdminErrorCodes.TargetUnknown, $"未知存储类型：{string.Join(", ", unknown)}");

        var protectedTargets = targetIds
            .Where(t => StorageDataClassCatalog.Find(t) is { ManualCleanupAllowed: false })
            .ToList();
        if (protectedTargets.Count > 0)
            throw new StorageAdminException(
                StorageAdminErrorCodes.TargetProtected, $"受保护类型不可人工清理：{string.Join(", ", protectedTargets)}");

        var cutoffUtc = request.CutoffUtc ?? DateTimeOffset.UtcNow.AddDays(-request.OlderThanDays!.Value);
        if (request.OlderThanDays is null && request.CutoffUtc is null)
            throw new ArgumentException("olderThanDays or cutoffUtc is required.");
        if (cutoffUtc >= DateTimeOffset.UtcNow)
            throw new ArgumentException("cutoffUtc must be in the past.");

        RemoveExpiredPreviews();
        var now = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var targetPreviews = new List<StorageCleanupTargetPreviewDto>();

        foreach (var targetId in targetIds)
        {
            ct.ThrowIfCancellationRequested();
            var definition = StorageDataClassCatalog.Require(targetId);

            long candidates = 0;
            var truncated = false;
            if (definition.RequiresDerivedHandler)
            {
                var handler = ResolveHandler(definition);
                var estimate = handler is null
                    ? new StorageDerivedEstimate { Warning = "处理器不可用" }
                    : await handler.EstimateAsync(cutoffUtc, ct);
                candidates = estimate.CandidateCount;
                if (estimate.Warning is not null)
                    warnings.Add(estimate.Warning);
            }
            else if (definition.Tables.Count > 0)
            {
                var databasePath = Path.Combine(_paths.DatabasesRoot, definition.DatabaseFile ?? StorageDataClassCatalog.PlatformDatabaseFile);
                if (File.Exists(databasePath))
                {
                    await using var connection = await StorageCleanupExecutor.OpenReadWriteConnectionAsync(databasePath, ct);
                    foreach (var mapping in definition.Tables)
                    {
                        var (count, wasTruncated) = await StorageCleanupExecutor.ProbeCandidatesAsync(
                            connection, mapping.Table, mapping.TimestampColumn, cutoffUtc.ToString("O"), ct);
                        candidates += count;
                        truncated |= wasTruncated;
                    }
                }
            }
            else if (definition.LogRoots.Count > 0)
            {
                (candidates, truncated) = await ProbeLogFilesAsync(definition, cutoffUtc, ct);
            }

            var snapshotClass = snapshot.Classes.FirstOrDefault(c => c.TargetId == targetId);
            targetPreviews.Add(new StorageCleanupTargetPreviewDto
            {
                TargetId = targetId,
                DisplayName = definition.DisplayName,
                ActionSummary = DescribeAction(definition),
                EstimatedCandidateRows = candidates,
                CandidatesTruncated = truncated,
                EstimatedBytes = snapshotClass?.EstimatedBytes,
                OldestUtc = snapshotClass?.OldestUtc,
            });
        }

        var hasCandidates = targetPreviews.Any(t => t.EstimatedCandidateRows > 0);
        if (!hasCandidates)
            warnings.Add("所选类型在截止时间之前没有可清理数据。");

        var previewId = Guid.NewGuid();
        var preview = new StorageCleanupPreviewDto
        {
            PreviewId = previewId,
            CatalogVersion = StorageDataClassCatalog.Version,
            PolicyRevision = policyRevision,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(PreviewLifetime),
            CutoffUtc = cutoffUtc,
            Targets = targetPreviews,
            Warnings = warnings,
            HasCandidates = hasCandidates,
        };

        _previews[previewId] = new PendingAdminPreview(
            preview, cutoffUtc, targetIds,
            [.. targetIds.Select(BuildActionsForTarget).SelectMany(a => a)]);
        return preview;
    }

    /// <summary>消费 preview 创建 durable 作业并立即返回（202 语义）；requestId 幂等。</summary>
    public async Task<StorageCleanupJob> CreateJobFromPreviewAsync(
        Guid previewId, string requestId, string trigger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("requestId is required.");

        if (_requestIdIndex.TryGetValue(requestId, out var existingJobId)
            && _jobStore.Get(existingJobId) is { } existing)
            return existing;

        RemoveExpiredPreviews();
        if (!_previews.TryRemove(previewId, out var pending))
            throw new StorageAdminException(
                StorageAdminErrorCodes.PreviewExpired, "预览不存在或已被消费，请重新生成预览。");
        if (pending.Preview.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new StorageAdminException(
                StorageAdminErrorCodes.PreviewExpired, "预览已过期，请重新生成预览。");

        var job = new StorageCleanupJob
        {
            JobId = Guid.NewGuid(),
            Trigger = trigger,
            CutoffUtc = pending.CutoffUtc,
            TargetIds = pending.TargetIds,
            Actions = pending.Actions,
            Budget = new StorageCleanupBudget(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await _jobStore.PersistAsync(job);
        await _jobStore.AppendEventAsync(job.JobId, "storage.maintenance.scheduled", counters: new Dictionary<string, long>
        {
            ["targets"] = job.TargetIds.Count,
        });
        _requestIdIndex[requestId] = job.JobId;

        var channel = trigger == "automatic" ? _automaticQueue : _manualQueue;
        await channel.Writer.WriteAsync(job.JobId, ct);
        return job;
    }

    /// <summary>自动过期入口（调度器使用）：语义目录翻译 + 固定 cutoff。</summary>
    public async Task<StorageCleanupJob> SubmitAutomaticAsync(
        IReadOnlyList<string> targetIds, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        var actions = targetIds
            .Select(StorageDataClassCatalog.Require)
            .SelectMany(definition => BuildActionsForTarget(definition.TargetId))
            .ToList();

        var job = new StorageCleanupJob
        {
            JobId = Guid.NewGuid(),
            Trigger = "automatic",
            CutoffUtc = cutoffUtc,
            TargetIds = targetIds.ToList(),
            Actions = actions,
            Budget = new StorageCleanupBudget(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await _jobStore.PersistAsync(job);
        await _automaticQueue.Writer.WriteAsync(job.JobId, ct);
        return job;
    }

    /// <summary>旧 /databases 端点直提交：语义目录翻译 + 固定 cutoff，人工优先级。</summary>
    public async Task<StorageCleanupJob> SubmitLegacyAsync(
        IReadOnlyList<string> semanticTargetIds, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        var targets = semanticTargetIds
            .Select(StorageDataClassCatalog.Require)
            .ToList();
        var job = new StorageCleanupJob
        {
            JobId = Guid.NewGuid(),
            Trigger = "legacy",
            CutoffUtc = cutoffUtc,
            TargetIds = [.. targets.Select(t => t.TargetId)],
            Actions = [.. targets.SelectMany(t => BuildActionsForTarget(t.TargetId))],
            Budget = new StorageCleanupBudget(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await _jobStore.PersistAsync(job);
        await _manualQueue.Writer.WriteAsync(job.JobId, ct);
        return job;
    }

    /// <summary>取消请求：只在批次边界生效；当前小批事务正常结束。</summary>
    public async Task RequestCancelAsync(Guid jobId)
    {
        var job = _jobStore.Get(jobId)
            ?? throw new StorageAdminException(StorageAdminErrorCodes.TargetUnknown, "作业不存在。");
        if (IsTerminal(job.Status))
            throw new StorageAdminException(
                StorageAdminErrorCodes.JobNotCancellable, $"作业已处于终态 {job.Status}。");

        job.Status = StorageCleanupJobStatus.Cancelling;
        await _jobStore.PersistAsync(job);
        if (_cancellations.TryGetValue(jobId, out var cts))
            cts.Cancel();
    }

    /// <summary>确认超预算作业继续（提高预算上限后重新入队）。</summary>
    public async Task ConfirmAsync(Guid jobId)
    {
        var job = _jobStore.Get(jobId)
            ?? throw new StorageAdminException(StorageAdminErrorCodes.TargetUnknown, "作业不存在。");
        if (job.Status != StorageCleanupJobStatus.NeedsConfirmation)
            throw new StorageAdminException(
                StorageAdminErrorCodes.JobNotCancellable, $"作业状态 {job.Status} 不需要确认。");

        job.Status = StorageCleanupJobStatus.Queued;
        job.Budget = job.Budget with { MaxRowsPerJob = job.Budget.MaxRowsPerJob * 10 };
        await _jobStore.PersistAsync(job);
        await _manualQueue.Writer.WriteAsync(jobId);
    }

    /// <summary>同步等待作业到达任一非运行状态（旧 /databases 端点与调度器复用）。</summary>
    public async Task<StorageCleanupJob> WaitForNextCheckpointAsync(
        Guid jobId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var job = _jobStore.Get(jobId);
            if (job is null)
                throw new StorageAdminException(StorageAdminErrorCodes.TargetUnknown, "作业不存在。");
            if (job.Status != StorageCleanupJobStatus.Queued && job.Status != StorageCleanupJobStatus.Running)
                return job;

            var completion = _completions.GetOrAdd(
                jobId, _ => new TaskCompletionSource<StorageCleanupJob>(TaskCreationOptions.RunContinuationsAsynchronously));
            var waitTask = completion.Task;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return job;

            await Task.WhenAny(waitTask, Task.Delay(remaining, ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    private IStorageDerivedTargetHandler? ResolveHandler(StorageDataClassCatalog.StorageDataClassDefinition definition) =>
        _derivedHandlers.FirstOrDefault(h => string.Equals(h.HandlerId, definition.HandlerId, StringComparison.Ordinal));

    private static string DescribeAction(StorageDataClassCatalog.StorageDataClassDefinition definition) =>
        definition.Tables.Any(t => t.ClearColumns is { Length: > 0 })
            ? $"清空早于截止时间的 Debug/元数据大字段，不删除行。"
            : definition.LogRoots.Count > 0
                ? "删除早于截止时间的日志文件。"
                : definition.RequiresDerivedHandler
                    ? "删除已确认冗余的派生数据（重新校验后执行）。"
                    : "删除早于截止时间的原始行。";

    internal static IReadOnlyList<StorageCleanupAction> BuildActionsForTarget(string targetId)
    {
        var definition = StorageDataClassCatalog.Require(targetId);
        var actions = new List<StorageCleanupAction>();
        if (definition.RequiresDerivedHandler)
        {
            actions.Add(new StorageCleanupAction
            {
                TargetId = definition.TargetId,
                Kind = StorageCleanupKind.DerivedHandler,
                HandlerId = definition.HandlerId,
            });
            return actions;
        }

        foreach (var mapping in definition.Tables)
            actions.Add(BuildAction(definition, mapping));
        if (definition.LogRoots.Count > 0)
        {
            actions.Add(new StorageCleanupAction
            {
                TargetId = definition.TargetId,
                Kind = StorageCleanupKind.DeleteLogFiles,
                LogRoots = [.. definition.LogRoots],
            });
        }

        return actions;
    }

    private static StorageCleanupAction BuildAction(
        StorageDataClassCatalog.StorageDataClassDefinition definition,
        StorageDataClassCatalog.StoragePhysicalTable mapping)
    {
        var kind = mapping.ClearColumns is { Length: > 0 }
            ? StorageCleanupKind.ClearField
            : mapping.ArchiveBeforeDelete
                ? StorageCleanupKind.ArchiveAndDeleteRows
                : StorageCleanupKind.DeleteRows;
        return new StorageCleanupAction
        {
            TargetId = definition.TargetId,
            Kind = kind,
            DatabaseFile = definition.DatabaseFile,
            Table = mapping.Table,
            TimestampColumn = mapping.TimestampColumn,
            ClearColumns = mapping.ClearColumns,
            HandlerId = definition.HandlerId,
        };
    }

    private async Task<(long Count, bool Truncated)> ProbeLogFilesAsync(
        StorageDataClassCatalog.StorageDataClassDefinition definition,
        DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        const int probeLimit = 5_000;
        long count = 0;
        foreach (var relativeRoot in definition.LogRoots)
        {
            ct.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(Path.Combine(_paths.DataRoot, relativeRoot));
            if (!root.StartsWith(_paths.DataRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
                continue;

            var pending = new Stack<string>([root]);
            while (pending.Count > 0 && count < probeLimit)
            {
                var current = pending.Pop();
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(current);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (count >= probeLimit)
                        break;
                    try
                    {
                        var info = new FileInfo(file);
                        if (StorageInventorySampler.IsLogFileName(info.Name) && info.LastWriteTimeUtc < cutoffUtc.UtcDateTime)
                            count++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单项失败跳过。
                    }
                }

                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(current))
                        pending.Push(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单项失败跳过。
                }
            }
        }

        return (count, count >= probeLimit);
    }

    private void RemoveExpiredPreviews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _previews)
        {
            if (pair.Value.Preview.ExpiresAtUtc <= now)
                _previews.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>语义 API 稳定业务异常（控制器映射 ProblemDetails 错误码）。</summary>
    public sealed class StorageAdminException(string errorCode, string message) : Exception(message)
    {
        public string ErrorCode { get; } = errorCode;
    }

    private sealed record PendingAdminPreview(
        StorageCleanupPreviewDto Preview,
        DateTimeOffset CutoffUtc,
        IReadOnlyList<string> TargetIds,
        IReadOnlyList<StorageCleanupAction> Actions);
}
