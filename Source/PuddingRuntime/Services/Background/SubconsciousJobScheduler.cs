using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Background;

/// <summary>
/// Decides whether durable subconscious jobs may be leased in the current runtime window.
/// </summary>
public sealed class SubconsciousJobScheduler
{
    private readonly ISubconsciousJobQueue _queue;
    private readonly IIdleDetector? _idleDetector;
    private readonly IOptions<SubconsciousOptions> _options;
    private readonly ILogger<SubconsciousJobScheduler> _logger;

    public SubconsciousJobScheduler(
        ISubconsciousJobQueue queue,
        IOptions<SubconsciousOptions> options,
        ILogger<SubconsciousJobScheduler> logger,
        IIdleDetector? idleDetector = null)
    {
        _queue = queue;
        _options = options;
        _logger = logger;
        _idleDetector = idleDetector;
    }

    public async Task<SubconsciousJobQueueItem?> TryLeaseNextAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var scheduling = _options.Value.Scheduling;
        if (!scheduling.Enabled)
        {
            await RecordSkipAsync(SubconsciousSchedulingSkipReasons.Disabled, ct: ct);
            return null;
        }

        if (_idleDetector is not null)
        {
            var idleDuration = _idleDetector.IdleDuration;
            if (idleDuration < TimeSpan.FromSeconds(scheduling.IdleCooldownSeconds))
            {
                await RecordSkipAsync(
                    SubconsciousSchedulingSkipReasons.Cooldown,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["idle_seconds"] = ((int)idleDuration.TotalSeconds).ToString(),
                        ["required_idle_seconds"] = scheduling.IdleCooldownSeconds.ToString(),
                    },
                    ct);
                return null;
            }
        }

        var stats = await _queue.GetStatsAsync(ct);
        if (stats.Processing >= scheduling.MaxGlobalConcurrentJobs)
        {
            await RecordSkipAsync(
                SubconsciousSchedulingSkipReasons.GlobalLimit,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["processing"] = stats.Processing.ToString(),
                    ["limit"] = scheduling.MaxGlobalConcurrentJobs.ToString(),
                },
                ct);
            return null;
        }

        var excludedWorkspaces = stats.ProcessingByWorkspace
            .Where(pair => pair.Value >= scheduling.MaxWorkspaceConcurrentJobs)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        var budgetExcludedWorkspaces = new HashSet<string>(StringComparer.Ordinal);

        if (scheduling.MaxJobsPerWorkspacePerHour > 0 && scheduling.BudgetWindowMinutes > 0)
        {
            var budgetWindowStart = DateTimeOffset.UtcNow.AddMinutes(-scheduling.BudgetWindowMinutes);
            var leaseCounts = await _queue.GetWorkspaceLeaseCountsAsync(budgetWindowStart, ct);
            foreach (var pair in leaseCounts)
            {
                if (pair.Value >= scheduling.MaxJobsPerWorkspacePerHour)
                {
                    excludedWorkspaces.Add(pair.Key);
                    budgetExcludedWorkspaces.Add(pair.Key);
                }
            }
        }

        var excludedSessions = stats.ProcessingBySession
            .Where(pair => pair.Value >= scheduling.MaxSessionConcurrentJobs)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (scheduling.DryRun)
        {
            if (stats.Pending + stats.Retrying <= 0)
            {
                await RecordSkipAsync(SubconsciousSchedulingSkipReasons.NoEligibleJob, ct: ct);
                return null;
            }

            await RecordSkipAsync(
                SubconsciousSchedulingSkipReasons.DryRun,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mode"] = "dry_run",
                },
                ct);
            return null;
        }

        var leased = await _queue.LeaseNextAsync(
            leaseOwner,
            leaseDuration,
            new SubconsciousJobLeaseQuery
            {
                ExcludedWorkspaceIds = excludedWorkspaces,
                ExcludedSessionIds = excludedSessions,
                MaxRetryCount = scheduling.MaxRetryAttempts,
            },
            ct);

        if (leased is null)
        {
            var reason = ResolveNoLeaseReason(excludedWorkspaces, excludedSessions, budgetExcludedWorkspaces);
            var details = reason == SubconsciousSchedulingSkipReasons.BudgetExhausted
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["budget_window_minutes"] = scheduling.BudgetWindowMinutes.ToString(),
                    ["max_jobs_per_workspace_per_hour"] = scheduling.MaxJobsPerWorkspacePerHour.ToString(),
                    ["excluded_workspace_count"] = budgetExcludedWorkspaces.Count.ToString(),
                }
                : null;
            await RecordSkipAsync(reason, details, ct);
        }
        else
        {
            _logger.LogDebug(
                "[SubconsciousScheduler] Lease allowed jobId={JobId} workspace={WorkspaceId} session={SessionId}",
                leased.JobId,
                leased.Job.WorkspaceId,
                leased.Job.SessionId);
        }

        return leased;
    }

    private Task RecordSkipAsync(
        string reason,
        IReadOnlyDictionary<string, string>? details = null,
        CancellationToken ct = default) =>
        _queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest
        {
            Reason = reason,
            Details = details ?? new Dictionary<string, string>(StringComparer.Ordinal),
        }, ct);

    private static string ResolveNoLeaseReason(
        IReadOnlyCollection<string> excludedWorkspaces,
        IReadOnlyCollection<string> excludedSessions,
        IReadOnlyCollection<string> budgetExcludedWorkspaces)
    {
        if (budgetExcludedWorkspaces.Count > 0)
            return SubconsciousSchedulingSkipReasons.BudgetExhausted;
        if (excludedWorkspaces.Count > 0)
            return SubconsciousSchedulingSkipReasons.WorkspaceLimit;
        if (excludedSessions.Count > 0)
            return SubconsciousSchedulingSkipReasons.SessionLimit;
        return SubconsciousSchedulingSkipReasons.NoEligibleJob;
    }
}
