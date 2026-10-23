using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Storage;

/// <summary>
/// What one maintenance tick did.
/// </summary>
/// <param name="Decision">The scheduling decision that governed this tick.</param>
/// <param name="Ran">Whether a reclamation batch actually ran.</param>
/// <param name="ReclaimedPages">Pages measurably removed (0 when nothing ran, or when nothing was reclaimed).</param>
/// <param name="WriterBusy">Whether the batch was abandoned because a writer held the database.</param>
public sealed record CodeIndexMaintenanceOutcome(
    CodeIndexMaintenanceDecision Decision,
    bool Ran,
    long ReclaimedPages,
    bool WriterBusy);

/// <summary>
/// Stateful driver for background reclamation: keeps the rolling counters the scheduler needs and
/// turns one call into one decision plus at most one batch.
/// <para>
/// The counters live here rather than in the scheduler so the scheduler stays a pure function, and
/// they live here rather than in the hosted service so this part can be tested without a host, a
/// timer or a DI container.
/// </para>
/// </summary>
public sealed class CodeIndexMaintenanceRunner
{
    private static readonly TimeSpan RollingWindow = TimeSpan.FromHours(1);

    private readonly SqliteCodeIndexStore _store;
    private readonly CodeIndexStoragePolicy _policy;
    private readonly Queue<DateTimeOffset> _recentBatches = new();

    private DateTimeOffset? _lastBatchAt;
    private int _consecutiveZeroBatches;

    /// <summary>Creates a runner over one store.</summary>
    /// <param name="store">Store whose database is maintained; must not be null.</param>
    /// <param name="policy">Threshold policy; defaults to <see cref="CodeIndexStoragePolicy.Default"/>.</param>
    public CodeIndexMaintenanceRunner(SqliteCodeIndexStore store, CodeIndexStoragePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _policy = policy ?? CodeIndexStoragePolicy.Default;
    }

    /// <summary>Whether the runner believes it is inside the reclaim state.</summary>
    public bool CurrentlyReclaiming { get; private set; }

    /// <summary>
    /// Runs one tick: sample, decide, and optionally execute one batch.
    /// </summary>
    /// <param name="now">Current time, supplied by the caller so tests own the clock.</param>
    /// <param name="writeIdle">
    /// Whether the caller has established that no indexing write is in flight. Passing
    /// <see langword="false"/> is always safe; passing <see langword="true"/> while a writer is
    /// active only costs one short busy timeout.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the tick did, including the decision and its reason.</returns>
    public async Task<CodeIndexMaintenanceOutcome> TickAsync(
        DateTimeOffset now,
        bool writeIdle,
        CancellationToken cancellationToken = default)
    {
        TrimRollingWindow(now);

        var stats = await _store.GetStorageStatsAsync(cancellationToken).ConfigureAwait(false);
        var state = new CodeIndexMaintenanceState(
            Now: now,
            Stats: stats,
            WriteIdle: writeIdle,
            CurrentlyReclaiming: CurrentlyReclaiming,
            LastBatchAt: _lastBatchAt,
            BatchesInLastHour: _recentBatches.Count,
            ConsecutiveZeroBatches: _consecutiveZeroBatches);

        var decision = CodeIndexMaintenanceScheduler.Decide(state, _policy);
        CurrentlyReclaiming = decision.NextReclaiming;

        if (!decision.ShouldRun)
            return new CodeIndexMaintenanceOutcome(decision, Ran: false, ReclaimedPages: 0, WriterBusy: false);

        CodeIndexReclaimResult result;
        try
        {
            result = await _store.ReclaimFreePagesAsync(decision.BatchPages, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
        {
            // SQLITE_BUSY: a writer holds the database. This is an expected outcome, not a failure,
            // and it must NOT count towards the zero-progress counter — otherwise three busy ticks
            // in a row would look like "reclamation frees nothing" and pause the loop.
            return new CodeIndexMaintenanceOutcome(decision, Ran: false, ReclaimedPages: 0, WriterBusy: true);
        }

        _lastBatchAt = now;
        _recentBatches.Enqueue(now);

        if (result.ReclaimedPages > 0)
        {
            _consecutiveZeroBatches = 0;
        }
        else
        {
            _consecutiveZeroBatches++;
            if (_consecutiveZeroBatches >= CodeIndexMaintenanceScheduler.ZeroProgressBatchesBeforePause)
            {
                // Let the scheduler see the pause on the next tick rather than spinning here.
                CurrentlyReclaiming = false;
            }
        }

        return new CodeIndexMaintenanceOutcome(decision, Ran: true, result.ReclaimedPages, WriterBusy: false);
    }

    private void TrimRollingWindow(DateTimeOffset now)
    {
        while (_recentBatches.Count > 0 && now - _recentBatches.Peek() >= RollingWindow)
            _recentBatches.Dequeue();
    }
}
