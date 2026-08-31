using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Configuration;
using PuddingCode.Goals;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Admin-facing scheduler control plane. Configuration remains the existing
/// TaskAutoDispatch section; operational scans share the same bounded runner as
/// the background worker and are serialized per workspace.
/// </summary>
public sealed class TaskSchedulerControlService(
    TaskAutoDispatchScanRunner scanRunner,
    IOptionsMonitor<TaskAutoDispatchOptions> options,
    IOptionsMonitor<TaskBoundGoalOptions> taskBoundOptions,
    IOptionsMonitor<GoalRunOptions> goalOptions,
    PuddingDataPaths dataPaths,
    TimeProvider timeProvider,
    ILogger<TaskSchedulerControlService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceGates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimeState> _runtime =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private readonly Channel<bool> _wakeSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false,
    });

    private string ConfigurationPath => dataPaths.SystemConfigFile("system.json");

    public TaskSchedulerStatusSnapshot GetStatus(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var current = options.CurrentValue;
        var runtime = _runtime.GetOrAdd(workspaceId, _ => new RuntimeState());
        var paused = current.PausedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal);
        TaskAutoDispatchScanSummary? summary;
        string? lastError;
        DateTimeOffset? failedAt;
        bool scanning;
        lock (runtime.Gate)
        {
            summary = runtime.LastSummary;
            lastError = runtime.LastError;
            failedAt = runtime.LastFailedAtUtc;
            scanning = runtime.Scanning;
        }

        var prerequisites = new TaskSchedulerPrerequisites
        {
            TaskBoundGoalsEnabled = taskBoundOptions.CurrentValue.Enabled,
            GoalRunsEnabled = goalOptions.CurrentValue.Enabled,
            GoalContinuationEnabled = goalOptions.CurrentValue.ContinuationEnabled,
        };
        var normalizedMode = TaskAutoDispatchOptions.NormalizeMode(current.Mode);
        var state = scanning
            ? "scanning"
            : !current.Enabled
                || TaskAutoDispatchOptions.IsDisabledMode(current.Mode)
                || !current.WorkspaceIds.Contains(workspaceId, StringComparer.Ordinal)
                ? "disabled"
                : paused
                    ? "paused"
                    : lastError is not null && failedAt >= summary?.CompletedAtUtc
                        ? "faulted"
                        : TaskAutoDispatchOptions.IsAuthoritativeMode(normalizedMode)
                            ? normalizedMode
                            : "shadow";
        var nextScan = current.Enabled && !paused && summary is not null
            ? summary.CompletedAtUtc + current.ScanInterval
            : (DateTimeOffset?)null;

        return new TaskSchedulerStatusSnapshot
        {
            WorkspaceId = workspaceId,
            State = state,
            Policy = ToPolicy(current, workspaceId),
            Prerequisites = prerequisites,
            LastScan = summary,
            LastError = lastError,
            LastFailedAtUtc = failedAt,
            NextScanEstimateUtc = nextScan,
        };
    }

    public async Task<TaskSchedulerStatusSnapshot> UpdatePolicyAsync(
        string workspaceId,
        TaskSchedulerPolicyUpdate request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(request);
        await _policyGate.WaitAsync(ct);
        try
        {
            var current = options.CurrentValue;
            if (request.ExpectedRevision != current.PolicyRevision)
            {
                throw new TaskSchedulerControlException(
                    "scheduler_policy_conflict",
                    $"调度策略已被其他会话修改（当前 revision={current.PolicyRevision}），请刷新后重试。");
            }

            var candidate = CopyWith(current, workspaceId, request);
            var errors = TaskAutoDispatchOptions.Validate(
                candidate,
                taskBoundOptions.CurrentValue,
                goalOptions.CurrentValue);
            if (errors.Count > 0)
                throw new TaskSchedulerControlException("scheduler_policy_invalid", string.Join("; ", errors));

            var root = await ReadConfigurationAsync(ct);
            var sectionKey = root
                .Select(entry => entry.Key)
                .FirstOrDefault(key => string.Equals(
                    key,
                    TaskAutoDispatchOptions.SectionName,
                    StringComparison.OrdinalIgnoreCase))
                ?? "taskAutoDispatch";
            var section = root[sectionKey] as JsonObject ?? new JsonObject();
            root[sectionKey] = section;
            WritePolicy(section, candidate, current.PolicyRevision + 1);
            await AtomicFileWriter.WriteAsync(ConfigurationPath, root.ToJsonString(JsonOptions), ct);
            await WaitForRevisionAsync(current.PolicyRevision + 1, ct);
            Signal();
            logger.LogInformation(
                "[TaskSchedulerControl] policy updated workspace={WorkspaceId} revision={OldRevision}->{NewRevision} enabled={Enabled} paused={Paused} mode={Mode}",
                workspaceId,
                current.PolicyRevision,
                current.PolicyRevision + 1,
                candidate.Enabled,
                candidate.PausedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal),
                candidate.Mode);
            return GetStatus(workspaceId);
        }
        finally
        {
            _policyGate.Release();
        }
    }

    public Task<TaskSchedulerStatusSnapshot> SetPausedAsync(
        string workspaceId,
        bool paused,
        int expectedRevision,
        CancellationToken ct = default)
    {
        var current = options.CurrentValue;
        return UpdatePolicyAsync(workspaceId, new TaskSchedulerPolicyUpdate
        {
            ExpectedRevision = expectedRevision,
            Enabled = current.Enabled
                && current.WorkspaceIds.Contains(workspaceId, StringComparer.Ordinal),
            Mode = current.Mode,
            ScanIntervalSeconds = (int)Math.Round(current.ScanInterval.TotalSeconds),
            CandidateLimit = current.CandidateLimit,
            MaxStartsPerScan = current.MaxStartsPerScan,
            EventDrivenEnabled = current.EventDrivenEnabled,
            Paused = paused,
        }, ct);
    }

    public async Task<TaskAutoDispatchScanSummary> RunScanAsync(
        string workspaceId,
        string trigger,
        bool allowWhenPaused,
        CancellationToken ct = default)
    {
        var current = options.CurrentValue;
        if (!current.Enabled || !current.WorkspaceIds.Contains(workspaceId, StringComparer.Ordinal))
            throw new TaskSchedulerControlException("scheduler_disabled", "该工作区的自动调度尚未启用。");
        if (TaskAutoDispatchOptions.IsDisabledMode(current.Mode))
            throw new TaskSchedulerControlException(
                "scheduler_disabled",
                "TaskAutoDispatch:Mode=disabled，调度已全关（staged 灰度第一档）。");
        if (!allowWhenPaused && current.PausedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal))
            throw new TaskSchedulerControlException("scheduler_paused", "该工作区的自动调度已暂停。");
        EnsureAuthoritativePrerequisites(current);

        var gate = _workspaceGates.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            throw new TaskSchedulerControlException("scheduler_scan_in_progress", "该工作区已有一轮调度正在执行。");
        var runtime = _runtime.GetOrAdd(workspaceId, _ => new RuntimeState());
        SetScanning(runtime, true);
        try
        {
            var summary = await scanRunner.RunAsync(
                workspaceId,
                current.Mode,
                current.CandidateLimit,
                trigger,
                ct);
            RecordSuccess(runtime, summary);
            return summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(runtime, ex);
            throw;
        }
        finally
        {
            SetScanning(runtime, false);
            gate.Release();
        }
    }

    public async Task<TaskAutoDispatchScanSummary> RunRepairAsync(
        string workspaceId,
        string trigger,
        CancellationToken ct = default)
    {
        var gate = _workspaceGates.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            throw new TaskSchedulerControlException("scheduler_scan_in_progress", "该工作区已有一轮调度正在执行。");
        var runtime = _runtime.GetOrAdd(workspaceId, _ => new RuntimeState());
        SetScanning(runtime, true);
        try
        {
            var summary = await scanRunner.RepairAsync(
                workspaceId,
                Math.Clamp(options.CurrentValue.CandidateLimit, 1, 500),
                trigger,
                ct);
            RecordSuccess(runtime, summary);
            return summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(runtime, ex);
            throw;
        }
        finally
        {
            SetScanning(runtime, false);
            gate.Release();
        }
    }

    public async Task WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        var bounded = delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : delay;
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(bounded, waitCts.Token);
        var signalTask = _wakeSignal.Reader.ReadAsync(waitCts.Token).AsTask();
        var completed = await Task.WhenAny(delayTask, signalTask);
        await completed;
        await waitCts.CancelAsync();
    }

    public void Signal() => _wakeSignal.Writer.TryWrite(true);

    private void EnsureAuthoritativePrerequisites(TaskAutoDispatchOptions current)
    {
        if (!TaskAutoDispatchOptions.IsAuthoritativeMode(current.Mode))
            return;
        if (!taskBoundOptions.CurrentValue.Enabled
            || !goalOptions.CurrentValue.Enabled
            || !goalOptions.CurrentValue.ContinuationEnabled)
        {
            throw new TaskSchedulerControlException(
                "scheduler_prerequisite_missing",
                "Authoritative 调度要求 TaskBoundGoals、GoalRuns 和 Goal continuation 全部启用。");
        }
    }

    private async Task<JsonObject> ReadConfigurationAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(ConfigurationPath))
                return new JsonObject();
            var node = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPath, ct));
            return node as JsonObject
                ?? throw new TaskSchedulerControlException("scheduler_config_invalid", "system.json 根节点不是对象。");
        }
        catch (TaskSchedulerControlException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new TaskSchedulerControlException(
                "scheduler_config_invalid",
                $"调度配置无法读取：{ex.Message}");
        }
    }

    private async Task WaitForRevisionAsync(int revision, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40 && options.CurrentValue.PolicyRevision < revision; attempt++)
            await Task.Delay(50, ct);
        if (options.CurrentValue.PolicyRevision < revision)
        {
            throw new TaskSchedulerControlException(
                "scheduler_config_reload_timeout",
                "策略已安全写入配置文件，但热加载尚未完成；请刷新或重启 Core。");
        }
    }

    private static TaskAutoDispatchOptions CopyWith(
        TaskAutoDispatchOptions current,
        string workspaceId,
        TaskSchedulerPolicyUpdate request)
    {
        var workspaces = current.WorkspaceIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, workspaceId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (request.Enabled)
            workspaces.Add(workspaceId);
        var paused = current.PausedWorkspaceIds
            .Where(value => !string.IsNullOrWhiteSpace(value)
                && !string.Equals(value, workspaceId, StringComparison.Ordinal))
            .ToList();
        if (request.Paused)
            paused.Add(workspaceId);
        return new TaskAutoDispatchOptions
        {
            Enabled = workspaces.Count > 0,
            Mode = request.Mode.Trim().ToLowerInvariant(),
            PolicyRevision = current.PolicyRevision + 1,
            WorkspaceIds = [.. workspaces.Distinct(StringComparer.Ordinal)],
            PausedWorkspaceIds = [.. paused.Distinct(StringComparer.Ordinal)],
            ScanInterval = TimeSpan.FromSeconds(request.ScanIntervalSeconds),
            MinimumIdle = current.MinimumIdle,
            CandidateLimit = request.CandidateLimit,
            MaxStartsPerScan = request.MaxStartsPerScan,
            TrackerStallThreshold = current.TrackerStallThreshold,
            TaskTypeRoutes = current.TaskTypeRoutes,
            EventDrivenEnabled = request.EventDrivenEnabled,
            IntentPollInterval = current.IntentPollInterval,
            IntentBatchSize = current.IntentBatchSize,
            IntentLease = current.IntentLease,
            IntentMaxAttempts = current.IntentMaxAttempts,
        };
    }

    private static void WritePolicy(JsonObject section, TaskAutoDispatchOptions value, int revision)
    {
        section["PolicyRevision"] = revision;
        section["Enabled"] = value.Enabled;
        section["Mode"] = value.Mode;
        section["WorkspaceIds"] = JsonSerializer.SerializeToNode(value.WorkspaceIds, JsonOptions);
        section["PausedWorkspaceIds"] = JsonSerializer.SerializeToNode(value.PausedWorkspaceIds, JsonOptions);
        section["ScanInterval"] = value.ScanInterval.ToString("c", CultureInfo.InvariantCulture);
        section["CandidateLimit"] = value.CandidateLimit;
        section["MaxStartsPerScan"] = value.MaxStartsPerScan;
        section["EventDrivenEnabled"] = value.EventDrivenEnabled;
    }

    private static TaskSchedulerPolicySnapshot ToPolicy(TaskAutoDispatchOptions value, string workspaceId) => new()
    {
        Revision = value.PolicyRevision,
        Enabled = value.Enabled && value.WorkspaceIds.Contains(workspaceId, StringComparer.Ordinal),
        Paused = value.PausedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal),
        Mode = value.Mode,
        ScanIntervalSeconds = (int)Math.Round(value.ScanInterval.TotalSeconds),
        MinimumIdleSeconds = (int)Math.Round(value.MinimumIdle.TotalSeconds),
        CandidateLimit = value.CandidateLimit,
        MaxStartsPerScan = value.MaxStartsPerScan,
        TrackerStallSeconds = (int)Math.Round(value.TrackerStallThreshold.TotalSeconds),
        EventDrivenEnabled = value.EventDrivenEnabled,
    };

    private static void SetScanning(RuntimeState state, bool scanning)
    {
        lock (state.Gate)
            state.Scanning = scanning;
    }

    private static void RecordSuccess(RuntimeState state, TaskAutoDispatchScanSummary summary)
    {
        lock (state.Gate)
        {
            state.LastSummary = summary;
            state.LastError = null;
            state.LastFailedAtUtc = null;
        }
    }

    private void RecordFailure(RuntimeState state, Exception ex)
    {
        lock (state.Gate)
        {
            state.LastError = ex is TaskSchedulerControlException known
                ? $"{known.Code}: {known.Message}"
                : $"{ex.GetType().Name}: {ex.Message}";
            state.LastFailedAtUtc = timeProvider.GetUtcNow();
        }
        logger.LogError(ex, "[TaskSchedulerControl] scheduler operation failed");
    }

    private sealed class RuntimeState
    {
        public object Gate { get; } = new();
        public bool Scanning { get; set; }
        public TaskAutoDispatchScanSummary? LastSummary { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset? LastFailedAtUtc { get; set; }
    }
}

public sealed record TaskSchedulerPolicyUpdate
{
    public required int ExpectedRevision { get; init; }
    public required bool Enabled { get; init; }
    public required bool Paused { get; init; }
    public required string Mode { get; init; }
    public required int ScanIntervalSeconds { get; init; }
    public required int CandidateLimit { get; init; }
    public required int MaxStartsPerScan { get; init; }
    public required bool EventDrivenEnabled { get; init; }
}

public sealed record TaskSchedulerPolicySnapshot
{
    public required int Revision { get; init; }
    public required bool Enabled { get; init; }
    public required bool Paused { get; init; }
    public required string Mode { get; init; }
    public required int ScanIntervalSeconds { get; init; }
    public required int MinimumIdleSeconds { get; init; }
    public required int CandidateLimit { get; init; }
    public required int MaxStartsPerScan { get; init; }
    public required int TrackerStallSeconds { get; init; }
    public required bool EventDrivenEnabled { get; init; }
}

public sealed record TaskSchedulerPrerequisites
{
    public required bool TaskBoundGoalsEnabled { get; init; }
    public required bool GoalRunsEnabled { get; init; }
    public required bool GoalContinuationEnabled { get; init; }
    public bool AuthoritativeReady =>
        TaskBoundGoalsEnabled && GoalRunsEnabled && GoalContinuationEnabled;
}

public sealed record TaskSchedulerStatusSnapshot
{
    public required string WorkspaceId { get; init; }
    public required string State { get; init; }
    public required TaskSchedulerPolicySnapshot Policy { get; init; }
    public required TaskSchedulerPrerequisites Prerequisites { get; init; }
    public TaskAutoDispatchScanSummary? LastScan { get; init; }
    public DateTimeOffset? NextScanEstimateUtc { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastFailedAtUtc { get; init; }
}

public sealed class TaskSchedulerControlException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
