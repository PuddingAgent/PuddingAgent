namespace PuddingCodeIndex.Contracts;

/// <summary>Where a resolved library budget came from (diagnostics + tests).</summary>
public enum CodeIndexLibraryBudgetSource
{
    /// <summary>No configuration declared a budget; the built-in conservative default is in force.</summary>
    Default,

    /// <summary>Taken from the host-wide configuration file (e.g. <c>&lt;DataRoot&gt;/config/code-index.json</c>).</summary>
    GlobalConfig,

    /// <summary>Taken from the project-local configuration file (e.g. <c>&lt;projectRoot&gt;/.pudding/code-index.json</c>).</summary>
    ProjectConfig,
}

/// <summary>How close a library is to its configured capacity budget.</summary>
public enum CodeIndexLibraryCapacityLevel
{
    /// <summary>Below the soft threshold.</summary>
    Ok,

    /// <summary>At or above the soft threshold but below the hard budget: report / degrade softly.</summary>
    SoftExceeded,

    /// <summary>At or above the hard budget: the library must not grow further.</summary>
    HardExceeded,
}

/// <summary>
/// Built-in fallback for the per-library capacity budget (ADR-089 §M2; user ruling 2026-09-24).
/// </summary>
/// <remarks>
/// The budget is a <b>configuration parameter and is not meant to be a compiled-in constant</b>: the values
/// here are only the fail-closed fallback used when no configuration file exists, is readable, or is valid.
/// The accepted sources and their precedence are:
/// project file (<c>&lt;projectRoot&gt;/.pudding/code-index.json</c>) &gt; global file
/// (<c>&lt;DataRoot&gt;/config/code-index.json</c>) &gt; these defaults.
/// <para>
/// <see cref="MaxLibraryBytes"/> is an exact byte count on purpose: the GB/GiB ambiguity must never be
/// resolved by assumption, so the fallback is stated in bytes and equals the <b>binary</b> gigabyte.
/// </para>
/// </remarks>
public static class CodeIndexLibraryBudgetDefaults
{
    /// <summary>
    /// 1 GiB = 1,073,741,824 bytes. To change the budget, edit the configuration file — do not edit this constant.
    /// </summary>
    public const long MaxLibraryBytes = 1L << 30;

    /// <summary>Fraction of <see cref="MaxLibraryBytes"/> at which the soft threshold trips (80%).</summary>
    public const double SoftRatio = 0.8d;
}

/// <summary>A resolved capacity budget for a single index library.</summary>
public sealed record CodeIndexLibraryBudget(
    long MaxBytes,
    double SoftRatio,
    CodeIndexLibraryBudgetSource Source,
    string? ConfigPath = null,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>Bytes at which the library starts being reported as approaching its budget.</summary>
    public long SoftBytes => (long)Math.Round(MaxBytes * SoftRatio, MidpointRounding.AwayFromZero);

    /// <summary>The fail-closed fallback budget (no configuration in force).</summary>
    public static CodeIndexLibraryBudget Fallback { get; } = new(
        CodeIndexLibraryBudgetDefaults.MaxLibraryBytes,
        CodeIndexLibraryBudgetDefaults.SoftRatio,
        CodeIndexLibraryBudgetSource.Default);
}

/// <summary>Outcome of comparing a library's measured footprint against its budget.</summary>
public sealed record CodeIndexLibraryCapacityReport(
    long UsedBytes,
    long MaxBytes,
    long SoftBytes,
    double UsageRatio,
    CodeIndexLibraryCapacityLevel Level)
{
    /// <summary>True once the hard budget is reached or exceeded.</summary>
    public bool IsOverBudget => Level == CodeIndexLibraryCapacityLevel.HardExceeded;
}
