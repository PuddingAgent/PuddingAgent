namespace PuddingRetrievalEval.Services;

/// <summary>
/// The "noise directory" definition used by the <b>noise rate</b> metric.
/// <para>
/// ADR-089 §2.2 measured seven mutually inconsistent exclusion rule sets in this repository.
/// U4-0 must not pick a winner (that is U4-1's job), so the instrument is deliberately defined as the
/// <b>union</b> of the three documented sets:
/// </para>
/// <list type="number">
/// <item><description><c>SearchGrepTool.DefaultExcludeDirs</c> — Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs:34 (12 entries)</description></item>
/// <item><description><c>IndexExcludePatterns.NoiseDirNames</c> — Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs:12-24 (28 entries)</description></item>
/// <item><description><c>FullTextIndexOptions.ExcludedDirectoryNames</c> — Source/PuddingFullTextIndex/FullTextIndexOptions.cs (33 entries)</description></item>
/// </list>
/// <para>
/// Being a union makes the instrument <b>conservative: it over-reports noise rather than under-reports
/// it</b>, which is the safe direction for a before/after comparison (the same instrument is applied to
/// every measurement, so a systematically high number still shows real movement). The union is a
/// hard-coded, version-controlled list on purpose: the evaluation component must not reference the
/// components that own those rule sets.
/// </para>
/// <para>
/// <b>Segment semantics</b>: every path segment (including the last) is compared case-insensitively
/// against the set, so a directory named <c>bin</c> is noise and a file named <c>bin</c> is noise too.
/// A file whose name merely <i>starts with</i> a noise name (e.g. <c>bin.cs</c>, <c>obj.json</c>) is
/// not noise — the match is exact per segment.
/// </para>
/// </summary>
public static class NoiseDirectoryRules
{
    /// <summary>Union of the three documented rule sets above (46 distinct segment names).</summary>
    public static IReadOnlySet<string> Segments { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // SearchGrepTool.DefaultExcludeDirs (12)
        "$outputWwwroot", "dist", "node_modules", "bin", "obj", ".git", ".pudding",
        "TestResults", "artifacts", "publish", ".venv", ".tmp",

        // IndexExcludePatterns.NoiseDirNames (28)
        ".vs", ".idea", ".vscode", "packages", "build", ".next", "out",
        "__pycache__", "venv", ".tox", ".eggs", "coverage", ".nyc_output",
        ".pytest_cache", ".pudding-code", "Debug", "Release", "x64", "x86", "ARM", "ARM64",

        // FullTextIndexOptions.ExcludedDirectoryNames (33)
        ".svn", ".hg", "target", ".mypy_cache", "vendor", "bower_components",
        ".nuxt", ".output", ".angular", ".cache", ".turbo", "tmp", "temp",
    };

    /// <summary>True when any path segment of <paramref name="path"/> is a noise directory name.</summary>
    public static bool IsNoisePath(string? path)
    {
        var normalized = PathIdentity.Normalize(path);
        if (normalized.Length == 0)
            return false;

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (Segments.Contains(segment))
                return true;

        return false;
    }

    /// <summary>Count of hits in <paramref name="hits"/> whose path is a noise path.</summary>
    public static int CountNoiseHits(IEnumerable<string> hits) => hits.Count(h => IsNoisePath(h));
}
