namespace PuddingCodeIndex.Contracts;

/// <summary>
/// Outcome of one explicit full maintenance run.
/// <para>
/// A maintenance run rewrites the database, so the report carries the evidence needed to decide
/// whether it is acceptable: before/after counters, per-table row counts on both sides, and an
/// explicit list of verification failures. An empty <see cref="VerificationFailures"/> is the only
/// signal that the run may be considered successful — it is deliberately not inferred from
/// "no exception was thrown".
/// </para>
/// </summary>
/// <param name="BackupPath">Where the pre-maintenance backup was written.</param>
/// <param name="BackupBytes">Size of that backup file, or 0 when no backup was produced.</param>
/// <param name="BackupVerified">Whether the backup passed its own integrity and row-count checks.</param>
/// <param name="Before">Snapshot taken before the rewrite.</param>
/// <param name="After">Snapshot taken after the rewrite.</param>
/// <param name="RowCountsBefore">Row count per table before the rewrite.</param>
/// <param name="RowCountsAfter">Row count per table after the rewrite.</param>
/// <param name="ReclaimedBytes">Bytes removed from the main file, floored at zero.</param>
/// <param name="VerificationFailures">Every failed acceptance check; empty means the run is clean.</param>
public sealed record CodeIndexMaintenanceReport(
    string BackupPath,
    long BackupBytes,
    bool BackupVerified,
    CodeIndexStorageStats Before,
    CodeIndexStorageStats After,
    IReadOnlyDictionary<string, long> RowCountsBefore,
    IReadOnlyDictionary<string, long> RowCountsAfter,
    long ReclaimedBytes,
    IReadOnlyList<string> VerificationFailures)
{
    /// <summary>Whether every acceptance check passed.</summary>
    public bool Succeeded => VerificationFailures.Count == 0;
}
