using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services;

/// <summary>
/// Measures an index library's footprint and compares it against a resolved budget (ADR-089 §M2).
/// </summary>
public static class CodeIndexLibraryCapacity
{
    /// <summary>
    /// Sums the on-disk size of every file under an index library directory.
    /// </summary>
    /// <remarks>
    /// The measurement deliberately covers the <b>whole library directory</b> — main database, <c>-wal</c>,
    /// <c>-shm</c>, vector segments and any temporary rebuild artefacts. That is the simplest rule that cannot
    /// under-count: excluding side files would let a library sit under its budget while its directory keeps
    /// growing. It is a policy choice, not a law of nature, and it is intentionally replaceable later.
    /// </remarks>
    /// <returns>Total bytes; 0 when the directory is missing or empty.</returns>
    public static long MeasureDirectoryBytes(string? libraryDirectory)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory) || !Directory.Exists(libraryDirectory))
            return 0;

        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(libraryDirectory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished mid-enumeration contributes 0 rather than failing the measurement.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best effort: a partially readable library still yields a lower bound.
        }

        return total;
    }

    /// <summary>Compares a measured footprint against a budget.</summary>
    public static CodeIndexLibraryCapacityReport Evaluate(CodeIndexLibraryBudget budget, long usedBytes)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var used = Math.Max(0L, usedBytes);
        var ratio = budget.MaxBytes <= 0 ? 1d : (double)used / budget.MaxBytes;

        var level = used >= budget.MaxBytes
            ? CodeIndexLibraryCapacityLevel.HardExceeded
            : used >= budget.SoftBytes
                ? CodeIndexLibraryCapacityLevel.SoftExceeded
                : CodeIndexLibraryCapacityLevel.Ok;

        return new CodeIndexLibraryCapacityReport(used, budget.MaxBytes, budget.SoftBytes, ratio, level);
    }
}
