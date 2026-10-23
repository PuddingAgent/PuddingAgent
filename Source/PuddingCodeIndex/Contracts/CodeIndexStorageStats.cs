namespace PuddingCodeIndex.Contracts;

/// <summary>
/// Storage accounting snapshot of one code-index database, read from SQLite pragmas.
/// <para>
/// Motivation (measured 2026-10-23 on the production index database): it held 194,231 free
/// pages = 758.7 MiB, i.e. 26.8 percent of the file, and never released them, because the
/// database had been created with <c>auto_vacuum=NONE</c>. These counters are the minimum a
/// maintenance policy needs in order to decide whether reclaiming is worthwhile.
/// </para>
/// </summary>
/// <param name="PageSize">Bytes per page (<c>PRAGMA page_size</c>).</param>
/// <param name="PageCount">Total pages in the database file (<c>PRAGMA page_count</c>).</param>
/// <param name="FreelistCount">Pages currently on the freelist (<c>PRAGMA freelist_count</c>).</param>
/// <param name="AutoVacuumMode"><c>PRAGMA auto_vacuum</c>: 0 none, 1 full, 2 incremental.</param>
/// <param name="DatabaseFileBytes">Size of the main database file on disk, or 0 when absent.</param>
/// <param name="WalFileBytes">Size of the <c>-wal</c> file on disk, or 0 when absent.</param>
public sealed record CodeIndexStorageStats(
    long PageSize,
    long PageCount,
    long FreelistCount,
    int AutoVacuumMode,
    long DatabaseFileBytes,
    long WalFileBytes)
{
    /// <summary>Value of <c>PRAGMA auto_vacuum</c> that enables batched reclamation.</summary>
    public const int IncrementalAutoVacuumMode = 2;

    /// <summary>Bytes allocated in the file but sitting on the freelist, i.e. unused right now.</summary>
    public long FreeBytes => PageSize * FreelistCount;

    /// <summary>Fraction of the file that is freelist pages, in [0, 1].</summary>
    public double FreeRatio => PageCount <= 0 ? 0d : (double)FreelistCount / PageCount;

    /// <summary>
    /// Whether batched reclamation can do anything at all. Without
    /// <c>auto_vacuum=INCREMENTAL</c>, <c>PRAGMA incremental_vacuum</c> is a silent no-op.
    /// </summary>
    public bool SupportsIncrementalReclaim => AutoVacuumMode == IncrementalAutoVacuumMode;
}

/// <summary>
/// Outcome of one batched reclamation attempt.
/// <para>
/// <see cref="ReclaimedPages"/> is measured as the freelist delta across the call, never inferred
/// from "the statement executed": <c>PRAGMA incremental_vacuum</c> reports no row count, and it
/// succeeds without reclaiming anything when the database is not in incremental mode.
/// </para>
/// </summary>
/// <param name="Before">Snapshot taken immediately before the attempt.</param>
/// <param name="After">Snapshot taken immediately after the attempt.</param>
/// <param name="ReclaimedPages">Freelist pages actually removed (0 when nothing happened).</param>
/// <param name="Executed">Whether <c>PRAGMA incremental_vacuum</c> was issued at all.</param>
/// <param name="SkipReason">Why the attempt was skipped, when <paramref name="Executed"/> is false.</param>
public sealed record CodeIndexReclaimResult(
    CodeIndexStorageStats Before,
    CodeIndexStorageStats After,
    long ReclaimedPages,
    bool Executed,
    string? SkipReason);
