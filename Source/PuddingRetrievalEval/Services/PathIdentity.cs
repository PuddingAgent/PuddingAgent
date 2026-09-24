using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEval.Services;

/// <summary>
/// Path comparison rules for expected-hit matching.
/// <para>
/// Windows-first, exactly like the retrieval stack it measures: comparison is
/// <b>case-insensitive</b> and treats <c>\</c> and <c>/</c> as the same separator. An expected hit
/// may be repository-relative while a probe returns an absolute path, so a hit also matches when its
/// normalised path <b>ends with</b> the normalised expected path on a separator boundary.
/// </para>
/// <para>
/// <see cref="Normalize"/> lower-cases, therefore it must NOT be used for display — reports print the
/// raw path returned by the probe. Keep the two concerns apart.
/// </para>
/// </summary>
public static class PathIdentity
{
    /// <summary>Normalised form used for comparison only: slashes unified, <c>.</c> segments removed, lower-cased.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var value = path.Replace('\\', '/').Trim();

        while (value.StartsWith("./", StringComparison.Ordinal))
            value = value[2..];

        value = CollapseSlashes(value);

        // A "." segment anywhere (not only leading) carries no information for comparison.
        while (value.Contains("/./", StringComparison.Ordinal))
            value = value.Replace("/./", "/", StringComparison.Ordinal);

        value = value.TrimEnd('/');

        return value.ToLowerInvariant();
    }

    /// <summary>
    /// Splits an expected hit into its path part and optional <c>#Symbol</c> part.
    /// The last <c>#</c> is the separator, so a path may not contain <c>#</c> (holds for this
    /// repository; enforced by the annotation checklist).
    /// </summary>
    public static (string Path, string? Symbol) SplitExpected(string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return (string.Empty, null);

        var value = expected.Trim();
        var hash = value.LastIndexOf('#');
        if (hash < 0 || hash == value.Length - 1)
            return (value, null);

        var symbol = value[(hash + 1)..].Trim();
        return symbol.Length == 0 ? (value, null) : (value[..hash], symbol);
    }

    /// <summary>
    /// True when <paramref name="hit"/> satisfies <paramref name="expected"/>.
    /// A symbol constraint is only enforced when the probe actually reports a symbol: engines that
    /// cannot report one are matched on path alone instead of being penalised for a missing field.
    /// </summary>
    public static bool MatchesHit(SearchProbeHit hit, string expected)
    {
        var (expectedPath, expectedSymbol) = SplitExpected(expected);
        var target = Normalize(expectedPath);
        if (target.Length == 0)
            return false;

        var actual = Normalize(hit.Path);
        if (actual.Length == 0)
            return false;

        if (!string.Equals(actual, target, StringComparison.Ordinal)
            && !actual.EndsWith("/" + target, StringComparison.Ordinal))
            return false;

        if (expectedSymbol is null || hit.Symbol is null)
            return true;

        return string.Equals(hit.Symbol.Trim(), expectedSymbol, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 0-based rank of the first hit satisfying <paramref name="expected"/>, or <c>-1</c> when none does.
    /// </summary>
    public static int FirstRankOf(IReadOnlyList<SearchProbeHit> rankedHits, string expected)
    {
        for (var i = 0; i < rankedHits.Count; i++)
            if (MatchesHit(rankedHits[i], expected))
                return i;

        return -1;
    }

    /// <summary>Normalised, de-duplicated expected hits (duplicates must not inflate recall denominators).</summary>
    public static IReadOnlyList<string> DistinctExpected(IEnumerable<string> expectedHits)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var expected in expectedHits)
        {
            var (path, symbol) = SplitExpected(expected);
            var key = Normalize(path) + "#" + (symbol ?? string.Empty).ToLowerInvariant();
            if (key.Length == 1)
                continue;

            if (seen.Add(key))
                result.Add(expected);
        }

        return result;
    }

    /// <summary>
    /// Collapses a ranked hit list to <b>one entry per file</b>, keeping the first (best) occurrence.
    /// Engines that return one hit per matching line would otherwise let a single chatty file fill the
    /// whole top-k window. This is the canonical view every accuracy/noise metric is computed on.
    /// </summary>
    public static IReadOnlyList<SearchProbeHit> DistinctByFile(IEnumerable<SearchProbeHit> rankedHits)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SearchProbeHit>();

        foreach (var hit in rankedHits)
        {
            var key = Normalize(hit.Path);
            if (key.Length == 0)
                continue;

            if (seen.Add(key))
                result.Add(hit);
        }

        return result;
    }

    private static string CollapseSlashes(string value)
    {
        if (!value.Contains("//", StringComparison.Ordinal))
            return value;

        var builder = new System.Text.StringBuilder(value.Length);
        var previousSlash = false;

        foreach (var ch in value)
        {
            var isSlash = ch == '/';
            if (isSlash && previousSlash)
                continue;

            builder.Append(ch);
            previousSlash = isSlash;
        }

        return builder.ToString();
    }
}
