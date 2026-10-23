namespace PuddingCodeIndex.Contracts;

/// <summary>
/// Inputs for one maintenance decision.
/// <para>
/// Everything the loop needs is supplied by the caller — including the clock and the "is the
/// writer idle" judgement — so the decision stays pure and every gate can be exercised without a
/// database, a timer or a host.
/// </para>
/// </summary>
/// <param name="Now">Current time, supplied by the caller so tests control it.</param>
/// <param name="Stats">Storage snapshot for this tick.</param>
/// <param name="WriteIdle">
/// Whether the caller has established that no indexing write is pending or in flight. Reclamation
/// must never compete with the writer: SQLite allows a single writer, and a batch that blocks
/// behind an index run is worse than no batch at all.
/// </param>
/// <param name="CurrentlyReclaiming">Whether the loop was inside the reclaim state on the last tick.</param>
/// <param name="LastBatchAt">When the last batch ran, or <see langword="null"/> if none has yet.</param>
/// <param name="BatchesInLastHour">Batches already run in the trailing hour.</param>
/// <param name="ConsecutiveZeroBatches">
/// How many consecutive batches reported zero reclaimed pages. Sustained zero progress means the
/// pragma is not freeing anything and the loop must stop rather than spin.
/// </param>
public sealed record CodeIndexMaintenanceState(
    DateTimeOffset Now,
    CodeIndexStorageStats Stats,
    bool WriteIdle,
    bool CurrentlyReclaiming,
    DateTimeOffset? LastBatchAt,
    int BatchesInLastHour,
    int ConsecutiveZeroBatches);

/// <summary>
/// One maintenance decision: whether to run a batch now, how large it may be, and the reclaim
/// state to carry into the next tick.
/// <para>
/// A skipped decision always carries a human-readable <see cref="Reason"/>. "Nothing happened" is
/// not an acceptable log line for a subsystem whose whole job is to prove it is either working or
/// deliberately waiting.
/// </para>
/// </summary>
/// <param name="ShouldRun">Whether a reclamation batch should run on this tick.</param>
/// <param name="BatchPages">Page budget for this batch; 0 when <paramref name="ShouldRun"/> is false.</param>
/// <param name="NextReclaiming">Reclaim state to persist for the next tick.</param>
/// <param name="Reason">Why this decision was taken; always populated.</param>
public sealed record CodeIndexMaintenanceDecision(
    bool ShouldRun,
    int BatchPages,
    bool NextReclaiming,
    string Reason);

/// <summary>
/// Pure scheduling logic for batched free-page reclamation.
/// <para>
/// The loop that owns a timer and a database lives elsewhere; this type only answers "may I run a
/// batch on this tick, and how much". Bounds come from the third review (GPT-6 Astra, 2026-10-23):
/// a batch gate per <see cref="MinimumSecondsBetweenBatches"/> and a rolling ceiling of
/// <see cref="MaxBatchesPerHour"/>, both treated as ceilings rather than as a schedule.
/// </para>
/// </summary>
public static class CodeIndexMaintenanceScheduler
{
    /// <summary>Minimum spacing between batches. Below this the loop is churning, not maintaining.</summary>
    public const int MinimumSecondsBetweenBatches = 60;

    /// <summary>Rolling ceiling per hour. A ceiling, not a target: idle-writer gating dominates.</summary>
    public const int MaxBatchesPerHour = 16;

    /// <summary>
    /// Consecutive zero-progress batches after which the loop pauses and leaves the reclaim state.
    /// Without this a database in a state where reclamation frees nothing would be polled forever.
    /// </summary>
    public const int ZeroProgressBatchesBeforePause = 3;

    /// <summary>
    /// Decides what this tick should do.
    /// </summary>
    /// <param name="state">Current inputs; must not be null.</param>
    /// <param name="policy">Threshold policy; defaults to <see cref="CodeIndexStoragePolicy.Default"/>.</param>
    /// <returns>The decision, including the reclaim state to carry forward.</returns>
    public static CodeIndexMaintenanceDecision Decide(
        CodeIndexMaintenanceState state,
        CodeIndexStoragePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        policy ??= CodeIndexStoragePolicy.Default;

        if (!policy.ShouldReclaim(state.Stats, state.CurrentlyReclaiming))
        {
            // Leaving the reclaim state is explicit: carrying the old value forward would pin the
            // loop in a state it has already decided to leave.
            return Skip(
                nextReclaiming: false,
                state.CurrentlyReclaiming
                    ? "free pages fell to the exit bound; leaving the reclaim state"
                    : "free pages are below the enter bound");
        }

        // Entering (or continuing) the reclaim state, but only if the work can actually progress.
        if (state.ConsecutiveZeroBatches >= ZeroProgressBatchesBeforePause)
        {
            return Skip(
                nextReclaiming: false,
                $"{state.ConsecutiveZeroBatches} consecutive batches reclaimed nothing; pausing and leaving the reclaim state");
        }

        if (!state.WriteIdle)
        {
            return Skip(state.CurrentlyReclaiming, "indexing writer is busy; waiting for an idle window");
        }

        if (state.LastBatchAt is { } last)
        {
            var since = state.Now - last;
            if (since < TimeSpan.FromSeconds(MinimumSecondsBetweenBatches))
            {
                return Skip(
                    state.CurrentlyReclaiming,
                    $"only {since.TotalSeconds:F0}s since the last batch; minimum spacing is {MinimumSecondsBetweenBatches}s");
            }
        }

        if (state.BatchesInLastHour >= MaxBatchesPerHour)
        {
            return Skip(
                state.CurrentlyReclaiming,
                $"already ran {state.BatchesInLastHour} batches in the last hour; ceiling is {MaxBatchesPerHour}");
        }

        return new CodeIndexMaintenanceDecision(
            ShouldRun: true,
            BatchPages: CodeIndexStoragePolicy.DefaultBatchPages,
            NextReclaiming: true,
            Reason: $"reclaiming: free {state.Stats.FreeBytes} bytes across {state.Stats.FreelistCount} pages "
                + $"({state.Stats.FreeRatio:P1} of the file)");
    }

    private static CodeIndexMaintenanceDecision Skip(bool nextReclaiming, string reason) =>
        new(ShouldRun: false, BatchPages: 0, NextReclaiming: nextReclaiming, Reason: reason);
}
