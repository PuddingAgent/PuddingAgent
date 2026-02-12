using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Background;

public sealed class SubconsciousRuntimeControlService : ISubconsciousRuntimeControl
{
    private readonly ISubconsciousJobQueue _queue;
    private readonly IOptions<SubconsciousOptions> _options;
    private readonly ISubconsciousDiagnosticLog? _diagnostics;
    private readonly object _stateLock = new();

    private bool _isPaused;
    private string? _lastCommand;
    private string? _reason;
    private string? _requestedBy;
    private DateTimeOffset _updatedAtUtc = DateTimeOffset.UtcNow;

    public SubconsciousRuntimeControlService(
        ISubconsciousJobQueue queue,
        IOptions<SubconsciousOptions> options,
        ISubconsciousDiagnosticLog? diagnostics = null)
    {
        _queue = queue;
        _options = options;
        _diagnostics = diagnostics;
    }

    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
            {
                return _isPaused;
            }
        }
    }

    public async Task<SubconsciousRuntimeControlSnapshot> StartAsync(
        SubconsciousRuntimeControlRequest request,
        CancellationToken ct = default)
    {
        UpdateState(isPaused: false, lastCommand: "start", request);
        var snapshot = await GetSnapshotAsync(ct);
        WriteControlEvent("subconscious.control.start", snapshot);
        return snapshot;
    }

    public async Task<SubconsciousRuntimeControlSnapshot> StopAsync(
        SubconsciousRuntimeControlRequest request,
        CancellationToken ct = default)
    {
        UpdateState(isPaused: true, lastCommand: "stop", request);
        var snapshot = await GetSnapshotAsync(ct);
        WriteControlEvent("subconscious.control.stop", snapshot);
        return snapshot;
    }

    public async Task<SubconsciousRuntimeControlSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        bool isPaused;
        string? lastCommand;
        string? reason;
        string? requestedBy;
        DateTimeOffset updatedAtUtc;

        lock (_stateLock)
        {
            isPaused = _isPaused;
            lastCommand = _lastCommand;
            reason = _reason;
            requestedBy = _requestedBy;
            updatedAtUtc = _updatedAtUtc;
        }

        return new SubconsciousRuntimeControlSnapshot
        {
            State = isPaused ? SubconsciousRuntimeStates.Paused : SubconsciousRuntimeStates.Running,
            IsPaused = isPaused,
            LastCommand = lastCommand,
            Reason = reason,
            RequestedBy = requestedBy,
            UpdatedAtUtc = updatedAtUtc,
            QueueStats = await _queue.GetStatsAsync(ct),
            Scheduling = BuildSchedulingSnapshot(),
            Diagnostics = BuildDiagnosticsSnapshot(),
        };
    }

    private void UpdateState(
        bool isPaused,
        string lastCommand,
        SubconsciousRuntimeControlRequest request)
    {
        lock (_stateLock)
        {
            _isPaused = isPaused;
            _lastCommand = lastCommand;
            _reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            _requestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? null : request.RequestedBy.Trim();
            _updatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private IReadOnlyDictionary<string, string> BuildSchedulingSnapshot()
    {
        var scheduling = _options.Value.Scheduling;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enabled"] = scheduling.Enabled.ToString().ToLowerInvariant(),
            ["dryRun"] = scheduling.DryRun.ToString().ToLowerInvariant(),
            ["idleCooldownSeconds"] = scheduling.IdleCooldownSeconds.ToString(),
            ["maxGlobalConcurrentJobs"] = scheduling.MaxGlobalConcurrentJobs.ToString(),
            ["maxWorkspaceConcurrentJobs"] = scheduling.MaxWorkspaceConcurrentJobs.ToString(),
            ["maxSessionConcurrentJobs"] = scheduling.MaxSessionConcurrentJobs.ToString(),
            ["maxRetryAttempts"] = scheduling.MaxRetryAttempts.ToString(),
            ["budgetWindowMinutes"] = scheduling.BudgetWindowMinutes.ToString(),
        };
    }

    private IReadOnlyDictionary<string, string> BuildDiagnosticsSnapshot()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["logName"] = "subconscious",
            ["logKind"] = "jsonl",
        };

        if (!string.IsNullOrWhiteSpace(_diagnostics?.LogDirectory))
            values["logDirectory"] = _diagnostics.LogDirectory!;

        return values;
    }

    private void WriteControlEvent(
        string name,
        SubconsciousRuntimeControlSnapshot snapshot)
    {
        _diagnostics?.Write(
            name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["state"] = snapshot.State,
                ["isPaused"] = snapshot.IsPaused,
                ["lastCommand"] = snapshot.LastCommand,
                ["reason"] = snapshot.Reason,
                ["requestedBy"] = snapshot.RequestedBy,
                ["pending"] = snapshot.QueueStats.Pending,
                ["retrying"] = snapshot.QueueStats.Retrying,
                ["processing"] = snapshot.QueueStats.Processing,
                ["completed"] = snapshot.QueueStats.Completed,
                ["deadLetter"] = snapshot.QueueStats.DeadLetter,
            });
    }
}
