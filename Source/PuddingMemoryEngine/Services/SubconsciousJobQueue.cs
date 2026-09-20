using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// SQLite-backed durable queue for subconscious maintenance jobs.
/// </summary>
public sealed class SubconsciousJobQueue : ISubconsciousJobQueue
{
    private const int MaxRetries = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly ILogger<SubconsciousJobQueue> _logger;
    private const string NoWorkspaceKey = "(none)";

    /// <summary>
    /// 高频 schedule skip（no eligible job / cooldown / dry run / disabled / 限额）的聚合窗口宽度。
    /// 窗口内的结果只累积在内存，窗口滚动时写一条 workspace 级 summary，
    /// 以消除约每 2 秒一对的 SQLite 双写放大。
    /// </summary>
    private static readonly TimeSpan SchedulingSkipWindow = TimeSpan.FromMinutes(5);

    private const int SchedulingSkipReasonDistributionMaxLength = 400;

    private readonly IRuntimeActivitySink? _activitySink;
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _leaseLock = new(1, 1);
    private readonly object _skipAccumulatorLock = new();
    private readonly Dictionary<string, SchedulingSkipAccumulator> _skipAccumulators =
        new(StringComparer.Ordinal);

    public SubconsciousJobQueue(
        IDbContextFactory<MemoryDbContext> dbFactory,
        ILogger<SubconsciousJobQueue> logger,
        IRuntimeActivitySink? activitySink = null,
        ITelemetryMetricSink? telemetrySink = null,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _activitySink = activitySink;
        _telemetrySink = telemetrySink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SubconsciousJobQueueItem> EnqueueAsync(
        SubconsciousJobEnqueueRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.SubconsciousJobs
            .FirstOrDefaultAsync(j => j.IdempotencyKey == request.IdempotencyKey, ct);

        if (existing is not null && existing.Status is not ("completed" or "dead_letter"))
            return ToItem(existing);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (existing is null)
        {
            existing = new SubconsciousJobEntity
            {
                JobId = Guid.NewGuid().ToString("N"),
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = now,
            };
            db.SubconsciousJobs.Add(existing);
        }

        existing.JobType = request.JobType;
        existing.Status = "pending";
        existing.WorkspaceId = request.Job.WorkspaceId;
        existing.SessionId = request.Job.SessionId;
        existing.AgentId = request.Job.AgentId;
        existing.AgentTemplateId = request.Job.AgentTemplateId;
        existing.SourceHookName = request.SourceHookName;
        existing.SourceEventId = request.SourceEventId;
        existing.SourceCompactionId = request.SourceCompactionId;
        existing.PayloadJson = JsonSerializer.Serialize(request.Job, JsonOptions);
        existing.RetryCount = 0;
        existing.LeaseOwner = null;
        existing.LeaseUntil = null;
        existing.AvailableAt = now;
        existing.StartedAt = null;
        existing.CompletedAt = null;
        existing.ErrorMessage = null;
        existing.UpdatedAt = now;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[SubconsciousJobQueue] Enqueued job id={JobId} type={JobType} session={SessionId}",
            existing.JobId,
            existing.JobType,
            existing.SessionId);

        var item = ToItem(existing);
        await RecordActivityAsync("subconscious_job.enqueue", RuntimeActivityStatuses.Succeeded, item, ct: ct);
        await RecordMetricAsync("subconscious_job.enqueue", TelemetryMetricStatuses.Succeeded, item, ct: ct);
        return item;
    }

    public async Task<SubconsciousJobQueueItem?> LeaseNextAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        SubconsciousJobLeaseQuery? query = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner))
            throw new ArgumentException("Lease owner is required.", nameof(leaseOwner));

        await _leaseLock.WaitAsync(ct);
        try
        {
            return await LeaseNextCoreAsync(leaseOwner, leaseDuration, query, ct);
        }
        finally
        {
            _leaseLock.Release();
        }
    }

    public async Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SubconsciousJobs
            .Select(j => new { j.Status, j.WorkspaceId, j.SessionId, j.LeaseUntil })
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var activeProcessing = rows
            .Where(j => j.Status == "processing" && j.LeaseUntil is not null && j.LeaseUntil > now)
            .ToArray();
        var expiredProcessingCount = rows.Count(j =>
            j.Status == "processing" && (j.LeaseUntil is null || j.LeaseUntil <= now));

        return new SubconsciousJobQueueStats
        {
            // An expired processing lease is immediately eligible for LeaseNextAsync,
            // so expose it as pending backlog instead of active concurrency. Otherwise
            // the scheduler's global limit can permanently prevent its own recovery.
            Pending = rows.Count(j => j.Status == "pending") + expiredProcessingCount,
            Retrying = rows.Count(j => j.Status == "retrying"),
            Processing = activeProcessing.Length,
            Completed = rows.Count(j => j.Status == "completed"),
            DeadLetter = rows.Count(j => j.Status == "dead_letter"),
            ProcessingByWorkspace = activeProcessing
                .GroupBy(j => j.WorkspaceId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            ProcessingBySession = activeProcessing
                .GroupBy(j => j.SessionId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };
    }

    public async Task<SubconsciousJobQueueItem?> FindLatestAsync(
        SubconsciousJobLookupQuery query,
        CancellationToken ct = default)
    {
        if (query is null)
            throw new ArgumentNullException(nameof(query));

        if (string.IsNullOrWhiteSpace(query.JobId)
            && string.IsNullOrWhiteSpace(query.IdempotencyKey)
            && string.IsNullOrWhiteSpace(query.SourceCompactionId))
        {
            throw new ArgumentException(
                "jobId, idempotencyKey or sourceCompactionId is required.",
                nameof(query));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var jobs = db.SubconsciousJobs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.JobId))
            jobs = jobs.Where(j => j.JobId == query.JobId.Trim());
        if (!string.IsNullOrWhiteSpace(query.IdempotencyKey))
            jobs = jobs.Where(j => j.IdempotencyKey == query.IdempotencyKey.Trim());
        if (!string.IsNullOrWhiteSpace(query.SourceHookName))
            jobs = jobs.Where(j => j.SourceHookName == query.SourceHookName.Trim());
        if (!string.IsNullOrWhiteSpace(query.SourceCompactionId))
            jobs = jobs.Where(j => j.SourceCompactionId == query.SourceCompactionId.Trim());
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
            jobs = jobs.Where(j => j.WorkspaceId == query.WorkspaceId.Trim());
        if (!string.IsNullOrWhiteSpace(query.SessionId))
            jobs = jobs.Where(j => j.SessionId == query.SessionId.Trim());

        var entity = await jobs
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToItem(entity);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sinceMs = since.ToUnixTimeMilliseconds();
        var rows = await db.SubconsciousJobs
            .Where(j => j.StartedAt != null && j.StartedAt >= sinceMs)
            .GroupBy(j => j.WorkspaceId)
            .Select(g => new { WorkspaceId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(row => row.WorkspaceId, row => row.Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// 记录一次调度 skip。<para>
    /// 高频 no-op（no eligible job / cooldown / dry run / disabled / 限额）<b>不再</b>逐事件双写
    /// runtime_activity + telemetry_metric，只按 5 分钟窗口累积并落一条 workspace 级 summary；
    /// telemetry metric 是该 fact 的唯一 authoritative owner。
    /// 派发、拒绝状态变化、错误仍由各自路径写明细。</para>
    /// </summary>
    public async Task RecordSchedulingSkipAsync(
        SubconsciousSchedulingSkipRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Scheduling skip reason is required.", nameof(request));

        var now = _timeProvider.GetUtcNow();
        var windowStartMs = ResolveSchedulingSkipWindowStartMs(now);
        var workspaceKey = request.WorkspaceId ?? NoWorkspaceKey;
        SchedulingSkipAccumulator? closedWindow = null;

        lock (_skipAccumulatorLock)
        {
            if (_skipAccumulators.TryGetValue(workspaceKey, out var accumulator)
                && accumulator.WindowStartMs != windowStartMs)
            {
                closedWindow = accumulator;
                _skipAccumulators.Remove(workspaceKey);
                accumulator = null;
            }

            if (accumulator is null)
            {
                accumulator = new SchedulingSkipAccumulator(windowStartMs);
                _skipAccumulators[workspaceKey] = accumulator;
            }

            accumulator.Add(request, now);
        }

        if (closedWindow is not null)
            await FlushSchedulingSkipSummaryAsync(workspaceKey, closedWindow, ct);
    }

    private static long ResolveSchedulingSkipWindowStartMs(DateTimeOffset now)
    {
        var windowMs = (long)SchedulingSkipWindow.TotalMilliseconds;
        var unixMs = now.ToUnixTimeMilliseconds();
        return unixMs - (unixMs % windowMs);
    }

    /// <summary>
    /// 写出一个已闭合窗口的 workspace 级 summary。
    /// runtime_activity 侧的同事实逐事件写入已移除（聚合后只保留单 owner + 抽样 trace 锚点）。
    /// </summary>
    private async Task FlushSchedulingSkipSummaryAsync(
        string workspaceKey,
        SchedulingSkipAccumulator accumulator,
        CancellationToken ct)
    {
        if (_telemetrySink is null)
            return;

        var total = accumulator.Total;
        var windowEndMs = accumulator.WindowStartMs + (long)SchedulingSkipWindow.TotalMilliseconds;
        var (reasonDistribution, reasonKinds, truncated) =
            accumulator.BuildReasonDistribution(SchedulingSkipReasonDistributionMaxLength);

        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace_id"] = workspaceKey,
            ["window_start_utc"] = FormatUnixMs(accumulator.WindowStartMs),
            ["window_end_utc"] = FormatUnixMs(windowEndMs),
            ["window_minutes"] = SchedulingSkipWindow.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture),
            ["skip_total"] = total.ToString(CultureInfo.InvariantCulture),
            ["coalesced_events"] = Math.Max(0, total - 1).ToString(CultureInfo.InvariantCulture),
            ["first_utc"] = FormatUnixMs(accumulator.FirstMs),
            ["last_utc"] = FormatUnixMs(accumulator.LastMs),
            ["reason_kinds"] = reasonKinds.ToString(CultureInfo.InvariantCulture),
            ["reason_distribution"] = reasonDistribution,
        };

        if (truncated)
            dimensions["reason_distribution_truncated"] = "true";

        AddIfPresent(dimensions, "sample_source_event_id", accumulator.SampleSourceEventId);
        AddIfPresent(dimensions, "sample_session_id", accumulator.SampleSessionId);

        try
        {
            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = RuntimeTraceContext.CreateNew(
                    sessionId: accumulator.SampleSessionId,
                    workspaceId: workspaceKey == NoWorkspaceKey ? null : workspaceKey,
                    eventId: accumulator.SampleSourceEventId),
                Source = "pudding.memory.subconscious_job_queue",
                Category = TelemetryMetricCategories.Memory,
                Name = "subconscious_job.schedule_skip.summary",
                Status = TelemetryMetricStatuses.Deferred,
                CountValue = total,
                Unit = "job",
                Severity = "info",
                Summary =
                    $"Subconscious job scheduling skipped {total} time(s) in {SchedulingSkipWindow.TotalMinutes:0.##}m window across {reasonKinds} reason(s)",
                Dimensions = dimensions,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[SubconsciousJobQueue] Failed to record scheduling skip summary workspace={WorkspaceId} windowStart={WindowStart}",
                workspaceKey,
                accumulator.WindowStartMs);
        }
    }

    private static string FormatUnixMs(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// 单个 (workspace, 5 分钟窗口) 的 skip 计数。窗口内的 skip 事件在此折叠为计数与原因分布，
    /// 并保留抽样 trace 锚点用于 drill-down。
    /// </summary>
    private sealed class SchedulingSkipAccumulator(long windowStartMs)
    {
        private readonly Dictionary<string, long> _reasonCounts = new(StringComparer.Ordinal);

        public long WindowStartMs { get; } = windowStartMs;

        public long FirstMs { get; private set; }

        public long LastMs { get; private set; }

        public long Total { get; private set; }

        public string? SampleSourceEventId { get; private set; }

        public string? SampleSessionId { get; private set; }

        public void Add(SubconsciousSchedulingSkipRequest request, DateTimeOffset now)
        {
            var nowMs = now.ToUnixTimeMilliseconds();
            if (Total == 0)
                FirstMs = nowMs;

            LastMs = nowMs;
            Total++;
            _reasonCounts[request.Reason] =
                _reasonCounts.TryGetValue(request.Reason, out var count) ? count + 1 : 1;
            SampleSourceEventId ??= request.SourceEventId;
            SampleSessionId ??= request.SessionId;
        }

        public (string Distribution, int ReasonKinds, bool Truncated) BuildReasonDistribution(int maxLength)
        {
            var ordered = _reasonCounts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")
                .ToList();

            var distribution = string.Join(",", ordered);
            if (distribution.Length <= maxLength)
                return (distribution, ordered.Count, false);

            // 截断时仍返回完整原因种类计数，只省略超限明细，避免丢失「总数/原因分布」事实。
            var kept = new List<string>();
            var length = 0;
            foreach (var entry in ordered)
            {
                var added = length == 0 ? entry.Length : entry.Length + 1;
                if (length + added > maxLength)
                    break;

                kept.Add(entry);
                length += added;
            }

            return (string.Join(",", kept), ordered.Count, true);
        }
    }

    public async Task RecordResultAsync(
        string jobId,
        string leaseOwner,
        SubconsciousJobResultEnvelope result,
        CancellationToken ct = default)
    {
        ValidateResult(result);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await FindLeasedJobAsync(db, jobId, leaseOwner, ct);
        entity.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
        entity.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
    }

    public async Task<SubconsciousJobResultEnvelope?> GetResultAsync(
        string jobId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job id is required.", nameof(jobId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var resultJson = await db.SubconsciousJobs
            .Where(j => j.JobId == jobId)
            .Select(j => j.ResultJson)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        return JsonSerializer.Deserialize<SubconsciousJobResultEnvelope>(resultJson, JsonOptions);
    }

    public async Task CompleteAsync(
        string jobId,
        string leaseOwner,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await FindLeasedJobAsync(db, jobId, leaseOwner, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        entity.Status = "completed";
        entity.CompletedAt = now;
        entity.LeaseOwner = null;
        entity.LeaseUntil = null;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        var item = ToItem(entity);
        await RecordActivityAsync("subconscious_job.complete", RuntimeActivityStatuses.Succeeded, item, ct: ct);
        await RecordMetricAsync("subconscious_job.complete", TelemetryMetricStatuses.Succeeded, item, ct: ct);
    }

    public async Task<string> RetryAsync(
        string jobId,
        string leaseOwner,
        string error,
        TimeSpan? retryDelay = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await FindLeasedJobAsync(db, jobId, leaseOwner, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        entity.RetryCount++;
        entity.ErrorMessage = error;
        entity.LeaseOwner = null;
        entity.LeaseUntil = null;
        entity.UpdatedAt = now;

        if (entity.RetryCount >= MaxRetries)
        {
            entity.Status = "dead_letter";
            entity.CompletedAt = now;
            await db.SaveChangesAsync(ct);
            await RecordActivityAsync(
                "subconscious_job.dead_letter",
                RuntimeActivityStatuses.Failed,
                ToItem(entity),
                error,
                ct);
            await RecordMetricAsync(
                "subconscious_job.dead_letter",
                TelemetryMetricStatuses.Failed,
                ToItem(entity),
                error,
                ct);
            return entity.Status;
        }

        var delay = retryDelay ?? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, entity.RetryCount) * 10, 300));
        entity.Status = "retrying";
        entity.AvailableAt = DateTimeOffset.UtcNow.Add(delay).ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        await RecordActivityAsync(
            "subconscious_job.retry",
            RuntimeActivityStatuses.Retried,
            ToItem(entity),
            error,
            ct);
        await RecordMetricAsync(
            "subconscious_job.retry",
            TelemetryMetricStatuses.Retried,
            ToItem(entity),
            error,
            ct);
        return entity.Status;
    }

    public async Task DeadLetterAsync(
        string jobId,
        string leaseOwner,
        string error,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await FindLeasedJobAsync(db, jobId, leaseOwner, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        entity.Status = "dead_letter";
        entity.CompletedAt = now;
        entity.ErrorMessage = error;
        entity.LeaseOwner = null;
        entity.LeaseUntil = null;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await RecordActivityAsync(
            "subconscious_job.dead_letter",
            RuntimeActivityStatuses.Failed,
            ToItem(entity),
            error,
            ct);
        await RecordMetricAsync(
            "subconscious_job.dead_letter",
            TelemetryMetricStatuses.Failed,
            ToItem(entity),
            error,
            ct);
    }

    private async Task<SubconsciousJobQueueItem?> LeaseNextCoreAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        SubconsciousJobLeaseQuery? query,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var leaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration).ToUnixTimeMilliseconds();

        var candidates = db.SubconsciousJobs
            .Where(j => (j.Status == "pending" || j.Status == "retrying" || j.Status == "processing")
                && j.AvailableAt <= now
                && (j.LeaseUntil == null || j.LeaseUntil <= now));

        if (query?.MaxRetryCount is not null)
            candidates = candidates.Where(j => j.RetryCount <= query.MaxRetryCount.Value);

        if (query?.ExcludedWorkspaceIds.Count > 0)
        {
            var excludedWorkspaceIds = query.ExcludedWorkspaceIds.ToArray();
            candidates = candidates.Where(j => !excludedWorkspaceIds.Contains(j.WorkspaceId));
        }

        if (query?.ExcludedSessionIds.Count > 0)
        {
            var excludedSessionIds = query.ExcludedSessionIds.ToArray();
            candidates = candidates.Where(j => !excludedSessionIds.Contains(j.SessionId));
        }

        var entity = await candidates
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return null;

        entity.Status = "processing";
        entity.StartedAt ??= now;
        entity.LeaseOwner = leaseOwner;
        entity.LeaseUntil = leaseUntil;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        var item = ToItem(entity);
        await RecordActivityAsync("subconscious_job.lease", RuntimeActivityStatuses.Started, item, ct: ct);
        await RecordMetricAsync("subconscious_job.lease", TelemetryMetricStatuses.Started, item, ct: ct);
        return item;
    }

    private async Task RecordActivityAsync(
        string operation,
        string status,
        SubconsciousJobQueueItem item,
        string? error = null,
        CancellationToken ct = default)
    {
        if (_activitySink is null)
            return;

        try
        {
            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = RuntimeTraceContext.CreateNew(
                    sessionId: item.Job.SessionId,
                    workspaceId: item.Job.WorkspaceId,
                    eventId: item.SourceEventId),
                Component = RuntimeActivityComponents.Memory,
                Operation = operation,
                Status = status,
                Summary = $"Subconscious job {item.Status}",
                Metadata = BuildActivityMetadata(item),
                ErrorMessage = error,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[SubconsciousJobQueue] Failed to record runtime activity operation={Operation} jobId={JobId}",
                operation,
                item.JobId);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildActivityMetadata(SubconsciousJobQueueItem item)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jobId"] = item.JobId,
            ["jobType"] = item.JobType,
            ["jobStatus"] = item.Status,
            ["idempotencyKey"] = item.IdempotencyKey,
            ["workspaceId"] = item.Job.WorkspaceId,
            ["sessionId"] = item.Job.SessionId,
            ["agentId"] = item.Job.AgentId,
            ["agentTemplateId"] = item.Job.AgentTemplateId,
            ["retryCount"] = item.RetryCount.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(item.LeaseOwner))
            metadata["leaseOwner"] = item.LeaseOwner!;
        if (item.LeaseUntil is not null)
            metadata["leaseUntil"] = item.LeaseUntil.Value.ToString();
        if (!string.IsNullOrWhiteSpace(item.SourceHookName))
            metadata["sourceHookName"] = item.SourceHookName!;
        if (!string.IsNullOrWhiteSpace(item.SourceEventId))
            metadata["sourceEventId"] = item.SourceEventId!;
        if (!string.IsNullOrWhiteSpace(item.SourceCompactionId))
            metadata["sourceCompactionId"] = item.SourceCompactionId!;

        return metadata;
    }

    private async Task RecordMetricAsync(
        string name,
        string status,
        SubconsciousJobQueueItem item,
        string? error = null,
        CancellationToken ct = default)
    {
        if (_telemetrySink is null)
            return;

        try
        {
            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = RuntimeTraceContext.CreateNew(
                    sessionId: item.Job.SessionId,
                    workspaceId: item.Job.WorkspaceId,
                    eventId: item.SourceEventId),
                Source = "pudding.memory.subconscious_job_queue",
                Category = TelemetryMetricCategories.Memory,
                Name = name,
                Status = status,
                CountValue = 1,
                Unit = "job",
                Severity = status == TelemetryMetricStatuses.Failed ? "warning" : "info",
                Summary = $"Subconscious job {item.Status}",
                Dimensions = BuildMetricDimensions(item),
                ErrorMessage = error,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[SubconsciousJobQueue] Failed to record telemetry metric name={MetricName} jobId={JobId}",
                name,
                item.JobId);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildMetricDimensions(SubconsciousJobQueueItem item)
    {
        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["job_id"] = item.JobId,
            ["job_type"] = item.JobType,
            ["job_status"] = item.Status,
            ["workspace_id"] = item.Job.WorkspaceId,
            ["session_id"] = item.Job.SessionId,
            ["agent_id"] = item.Job.AgentId,
            ["agent_template_id"] = item.Job.AgentTemplateId,
            ["retry_count"] = item.RetryCount.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(item.LeaseOwner))
            dimensions["lease_owner"] = item.LeaseOwner!;
        if (!string.IsNullOrWhiteSpace(item.SourceHookName))
            dimensions["source_hook_name"] = item.SourceHookName!;
        if (!string.IsNullOrWhiteSpace(item.SourceEventId))
            dimensions["source_event_id"] = item.SourceEventId!;
        if (!string.IsNullOrWhiteSpace(item.SourceCompactionId))
            dimensions["source_compaction_id"] = item.SourceCompactionId!;

        return dimensions;
    }

    private static void AddIfPresent(
        IDictionary<string, string> fields,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields[key] = value;
    }

    private static async Task<SubconsciousJobEntity> FindLeasedJobAsync(
        MemoryDbContext db,
        string jobId,
        string leaseOwner,
        CancellationToken ct)
    {
        var entity = await db.SubconsciousJobs.FirstOrDefaultAsync(j => j.JobId == jobId, ct)
            ?? throw new InvalidOperationException($"Subconscious job '{jobId}' was not found.");

        if (!string.Equals(entity.LeaseOwner, leaseOwner, StringComparison.Ordinal))
            throw new InvalidOperationException($"Subconscious job '{jobId}' is not leased by '{leaseOwner}'.");

        return entity;
    }

    private static SubconsciousJobQueueItem ToItem(SubconsciousJobEntity entity)
    {
        var job = JsonSerializer.Deserialize<ConsolidationJob>(entity.PayloadJson, JsonOptions)
            ?? new ConsolidationJob
            {
                SessionId = entity.SessionId,
                WorkspaceId = entity.WorkspaceId,
                AgentId = entity.AgentId,
                AgentTemplateId = entity.AgentTemplateId,
            };

        return new SubconsciousJobQueueItem
        {
            JobId = entity.JobId,
            JobType = entity.JobType,
            IdempotencyKey = entity.IdempotencyKey,
            Status = entity.Status,
            RetryCount = entity.RetryCount,
            LeaseOwner = entity.LeaseOwner,
            LeaseUntil = entity.LeaseUntil,
            SourceHookName = entity.SourceHookName,
            SourceEventId = entity.SourceEventId,
            SourceCompactionId = entity.SourceCompactionId,
            Job = job,
        };
    }

    private static void ValidateRequest(SubconsciousJobEnqueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobType))
            throw new ArgumentException("Job type is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Job.SessionId)
            || string.IsNullOrWhiteSpace(request.Job.WorkspaceId)
            || string.IsNullOrWhiteSpace(request.Job.AgentId)
            || string.IsNullOrWhiteSpace(request.Job.AgentTemplateId))
            throw new ArgumentException("Consolidation job identity fields are required.", nameof(request));
    }

    private static void ValidateResult(SubconsciousJobResultEnvelope result)
    {
        if (string.IsNullOrWhiteSpace(result.Kind))
            throw new ArgumentException("Result kind is required.", nameof(result));
        if (string.IsNullOrWhiteSpace(result.Status))
            throw new ArgumentException("Result status is required.", nameof(result));
        if (string.IsNullOrWhiteSpace(result.Schema))
            throw new ArgumentException("Result schema is required.", nameof(result));
    }
}
