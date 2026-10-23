namespace PuddingCodeIndex.Contracts;

/// <summary>
/// Hysteresis policy deciding when an index database should reclaim free pages in batches.
/// <para>
/// The bounds come from the third review (GPT-6 Astra, 2026-10-23) and deliberately form a band
/// rather than a single cut-off: reclaiming starts only when both the absolute and the relative
/// amount of free space are high, and stops as soon as either drops below a lower bound that
/// still leaves headroom for the next re-index. A single threshold would make the maintenance
/// loop oscillate around it.
/// </para>
/// <para>
/// The policy is pure: it performs no IO and holds no state, so it can be exercised at every
/// boundary without touching a database. The loop that owns state lives elsewhere.
/// </para>
/// </summary>
/// <param name="EnterFreeBytes">Absolute free-space floor to start reclaiming.</param>
/// <param name="EnterFreeRatio">Relative free-space floor to start reclaiming.</param>
/// <param name="ExitFreeBytes">Absolute free-space level at which reclaiming stops.</param>
/// <param name="ExitFreeRatio">Relative free-space level at which reclaiming stops.</param>
public sealed record CodeIndexStoragePolicy(
    long EnterFreeBytes,
    double EnterFreeRatio,
    long ExitFreeBytes,
    double ExitFreeRatio)
{
    /// <summary>
    /// Default band: enter at 256 MiB <b>and</b> 20 percent free, exit at 128 MiB <b>or</b>
    /// 10 percent free. Both thresholds must be cleared to enter, which keeps a small file with
    /// a high free ratio and a large file with a low ratio from triggering.
    /// </summary>
    public static CodeIndexStoragePolicy Default { get; } =
        new(256L * 1024 * 1024, 0.20d, 128L * 1024 * 1024, 0.10d);

    /// <summary>
    /// Pages reclaimed per batch: 128 pages = 512 KiB at a 4096-byte page size. The starting batch
    /// is deliberately small; the review's guidance is to raise it towards 1024 pages only after
    /// measuring the batch cost, targeting well under 100 ms per batch.
    /// </summary>
    public const int DefaultBatchPages = 128;

    /// <summary>
    /// Whether the maintenance loop should be reclaiming right now.
    /// </summary>
    /// <param name="stats">Current storage snapshot.</param>
    /// <param name="currentlyReclaiming">
    /// Whether the loop is already inside the reclaim state. While inside, the exit bounds apply;
    /// that switch is what makes this policy hysteretic rather than a single cut-off.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when reclamation should run; <see langword="false"/> when it should
    /// not start, should stop, or cannot do anything (database not in incremental mode).
    /// </returns>
    public bool ShouldReclaim(CodeIndexStorageStats stats, bool currentlyReclaiming)
    {
        ArgumentNullException.ThrowIfNull(stats);

        // Without incremental auto-vacuum the reclamation pragma is a silent no-op, so a loop that
        // "entered" would spin forever making no progress.
        if (!stats.SupportsIncrementalReclaim)
            return false;

        var freeBytes = stats.FreeBytes;
        var freeRatio = stats.FreeRatio;

        return currentlyReclaiming
            ? freeBytes > ExitFreeBytes && freeRatio > ExitFreeRatio
            : freeBytes >= EnterFreeBytes && freeRatio >= EnterFreeRatio;
    }
}
