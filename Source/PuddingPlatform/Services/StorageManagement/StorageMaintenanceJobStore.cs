using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>清理动作类型（由语义目录固定翻译，客户端不可指定）。</summary>
public enum StorageCleanupKind
{
    ClearField = 0,
    DeleteRows = 1,
    ArchiveAndDeleteRows = 2,
    DeleteLogFiles = 3,
    DerivedHandler = 4,
}

/// <summary>Preview 固化的执行计划项。</summary>
public sealed record StorageCleanupAction
{
    public required string TargetId { get; init; }
    public required StorageCleanupKind Kind { get; init; }
    public string? DatabaseFile { get; init; }
    public string? Table { get; init; }
    public string? TimestampColumn { get; init; }
    public string[]? ClearColumns { get; init; }
    public string[]? LogRoots { get; init; }
    public string? HandlerId { get; init; }
}

/// <summary>在线安全预算（ADR-076 §5.4；人工/自动共用，不允许客户端调大）。</summary>
public sealed record StorageCleanupBudget
{
    public int BatchSize { get; init; } = 100;
    public int BatchDelayMs { get; init; } = 250;
    public int MaxBatchesPerTargetPerRound { get; init; } = 200;
    public int SliceSeconds { get; init; } = 30;
    public long MaxRowsPerJob { get; init; } = 2_000_000;
}

/// <summary>durable 清理作业（内存权威 + DataRoot 原子快照）。</summary>
public sealed record StorageCleanupJob
{
    public required Guid JobId { get; init; }
    public required string Trigger { get; init; }
    public StorageCleanupJobStatus Status { get; set; } = StorageCleanupJobStatus.Queued;
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public required DateTimeOffset CutoffUtc { get; init; }
    public required IReadOnlyList<string> TargetIds { get; init; }
    public required IReadOnlyList<StorageCleanupAction> Actions { get; init; }
    public required StorageCleanupBudget Budget { get; set; }

    public long DiscoveredRows { get; set; }
    public long ProcessedRows { get; set; }
    public long DeletedRows { get; set; }
    public long ClearedRows { get; set; }
    public long SkippedRows { get; set; }
    public long FailedRows { get; set; }
    public long DeletedFiles { get; set; }
    public long ReusableBytesEstimate { get; set; }
    /// <summary>每语义目标的处理行数（旧 /databases 端点结果映射用）。</summary>
    public Dictionary<string, long> TargetProcessed { get; set; } = new(StringComparer.Ordinal);
    /// <summary>每语义目标的处理单元数（作用域数/索引数）。</summary>
    public Dictionary<string, long> TargetUnits { get; set; } = new(StringComparer.Ordinal);

    /// <summary>每动作持久化 cursor（最后处理水位/目录位置），崩溃后从此续行。</summary>
    public Dictionary<string, string> Cursors { get; set; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; set; } = [];
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public StorageCleanupJobDto ToDto() => new()
    {
        JobId = JobId,
        Trigger = Trigger,
        Status = Status,
        CreatedAtUtc = CreatedAtUtc,
        StartedAtUtc = StartedAtUtc,
        FinishedAtUtc = FinishedAtUtc,
        CutoffUtc = CutoffUtc,
        TargetIds = TargetIds,
        Progress = new StorageCleanupJobProgressDto
        {
            DiscoveredRows = DiscoveredRows,
            ProcessedRows = ProcessedRows,
            DeletedRows = DeletedRows,
            ClearedRows = ClearedRows,
            SkippedRows = SkippedRows,
            FailedRows = FailedRows,
            DeletedFiles = DeletedFiles,
            ReusableBytesEstimate = ReusableBytesEstimate,
        },
        Warnings = Warnings,
        ErrorCode = ErrorCode,
        ErrorMessage = ErrorMessage,
    };
}

/// <summary>
/// ADR-076 §6.3 维护作业事实不写入被清理的 platform.db：
/// &lt;DataRoot&gt;/maintenance/storage/jobs/&lt;jobId&gt;/job.json 原子快照 + 有界 events.jsonl。
/// 完成记录保留 90 天且最多 1,000 个作业。
/// </summary>
public sealed class StorageMaintenanceJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private const int MaxJobs = 1_000;
    private const int MaxEventsPerJob = 2_000;

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<StorageMaintenanceJobStore> _logger;
    private readonly ConcurrentDictionary<Guid, StorageCleanupJob> _jobs = new();

    public StorageMaintenanceJobStore(PuddingDataPaths paths, ILogger<StorageMaintenanceJobStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string JobsRoot => Path.Combine(_paths.DataRoot, "maintenance", "storage", "jobs");

    private string JobDirectory(Guid jobId) => Path.Combine(JobsRoot, jobId.ToString("N"));

    private string JobFile(Guid jobId) => Path.Combine(JobDirectory(jobId), "job.json");

    private string EventsFile(Guid jobId) => Path.Combine(JobDirectory(jobId), "events.jsonl");

    /// <summary>启动恢复：读取磁盘作业；running/queued 复位 queued（从 cursor 续行），cancelling 收敛 cancelled。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(JobsRoot))
                return;

            foreach (var directory in Directory.EnumerateDirectories(JobsRoot))
            {
                ct.ThrowIfCancellationRequested();
                var file = Path.Combine(directory, "job.json");
                if (!File.Exists(file))
                    continue;
                try
                {
                    var job = JsonSerializer.Deserialize<StorageCleanupJob>(
                        await File.ReadAllTextAsync(file, ct), JsonOptions);
                    if (job is null)
                        continue;

                    if (job.Status is StorageCleanupJobStatus.Running or StorageCleanupJobStatus.PausedBusy)
                        job.Status = StorageCleanupJobStatus.Queued;
                    else if (job.Status == StorageCleanupJobStatus.Cancelling)
                    {
                        job.Status = StorageCleanupJobStatus.Cancelled;
                        job.FinishedAtUtc ??= DateTimeOffset.UtcNow;
                    }

                    _jobs[job.JobId] = job;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "[StorageJobs] failed to restore job file={File}", file);
                }
            }

            _logger.LogInformation("[StorageJobs] restored {Count} jobs", _jobs.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageJobs] failed to scan jobs root");
        }
    }

    public StorageCleanupJob? Get(Guid jobId) => _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IReadOnlyList<StorageCleanupJob> ListRecent(int limit = 50) =>
        _jobs.Values
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();

    public async Task PersistAsync(StorageCleanupJob job, CancellationToken ct = default)
    {
        _jobs[job.JobId] = job;
        try
        {
            Directory.CreateDirectory(JobDirectory(job.JobId));
            await AtomicFileWriter.WriteAsync(
                JobFile(job.JobId),
                JsonSerializer.Serialize(job, JsonOptions),
                ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageJobs] failed to persist job={JobId}", job.JobId);
        }
    }

    public async Task AppendEventAsync(
        Guid jobId, string kind, string? targetId = null,
        IReadOnlyDictionary<string, long>? counters = null, string? message = null,
        CancellationToken ct = default)
    {
        try
        {
            var entry = new StorageCleanupJobEventDto
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = kind,
                TargetId = targetId,
                Counters = counters,
                Message = message,
            };
            await File.AppendAllTextAsync(
                EventsFile(jobId),
                JsonSerializer.Serialize(entry, JsonOptions) + "\n",
                ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageJobs] failed to append event job={JobId}", jobId);
        }
    }

    public async Task<IReadOnlyList<StorageCleanupJobEventDto>> ReadEventsAsync(
        Guid jobId, int limit = 200, CancellationToken ct = default)
    {
        var events = new List<StorageCleanupJobEventDto>();
        try
        {
            var file = EventsFile(jobId);
            if (!File.Exists(file))
                return events;

            var lines = new List<string>();
            string? line;
            using var reader = new StreamReader(file);
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }

            foreach (var entry in lines.TakeLast(Math.Clamp(limit, 1, MaxEventsPerJob)))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<StorageCleanupJobEventDto>(entry, JsonOptions);
                    if (parsed is not null)
                        events.Add(parsed);
                }
                catch (JsonException)
                {
                    // 跳过半写行。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageJobs] failed to read events job={JobId}", jobId);
        }

        return events;
    }

    /// <summary>有界轮转：删除 90 天前或超额的终态作业目录。</summary>
    public async Task PruneAsync(CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(JobsRoot))
                return;

            var cutoff = DateTimeOffset.UtcNow - Retention;
            var candidates = new List<(string Directory, DateTimeOffset Created)>();
            foreach (var directory in Directory.EnumerateDirectories(JobsRoot))
            {
                var file = Path.Combine(directory, "job.json");
                if (!File.Exists(file))
                {
                    candidates.Add((directory, DateTimeOffset.MinValue));
                    continue;
                }

                try
                {
                    var job = JsonSerializer.Deserialize<StorageCleanupJob>(
                        await File.ReadAllTextAsync(file, ct), JsonOptions);
                    if (job is null)
                        continue;
                    if (job.Status is StorageCleanupJobStatus.Queued
                        or StorageCleanupJobStatus.Running
                        or StorageCleanupJobStatus.PausedBusy
                        or StorageCleanupJobStatus.NeedsConfirmation
                        or StorageCleanupJobStatus.Cancelling)
                    {
                        continue;
                    }

                    candidates.Add((directory, job.CreatedAtUtc));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // 无法解析的目录保留，避免误删活动作业。
                }
            }

            var removable = candidates
                .Where(c => c.Created < cutoff)
                .Select(c => c.Directory)
                .ToList();
            var excess = candidates
                .OrderByDescending(c => c.Created)
                .Skip(MaxJobs)
                .Select(c => c.Directory);
            removable.AddRange(excess);

            foreach (var directory in removable.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "[StorageJobs] failed to prune directory={Directory}", directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[StorageJobs] prune scan failed");
        }
    }
}
