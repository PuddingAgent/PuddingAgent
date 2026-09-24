using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEval.Services;

/// <summary>
/// Accuracy metrics over one ranked hit list and one annotated expectation list. Pure functions —
/// no IO, no engine, no thresholds.
/// <para>
/// <b>Canonicalisation</b>: annotations are file-level (<c>expectedHits</c> are file paths), but some
/// engines return one hit per matching <i>line</i>. Every metric therefore works on the
/// <b>distinct-file</b> view of the ranked list (first occurrence wins, see
/// <see cref="PathIdentity.DistinctByFile"/>). Without this, one chatty file could crowd a whole
/// top-10 window and <c>recall@5</c> would measure line noise instead of retrieval quality.
/// </para>
/// <para>
/// <b>Denominators, stated once and for all:</b>
/// <list type="bullet">
/// <item><description><c>recall@k</c> = |distinct expected matched inside top-k| / |distinct expected|; 0 when there are no expectations.</description></item>
/// <item><description><c>MRR</c> = 1 / (1-based rank of the first hit matching any expected); 0 when nothing matches.</description></item>
/// <item><description><c>precision@k</c> = |top-k hits matching some expected| / <b>k</b> (k, not the returned count — a short result list is a real loss and must show up).</description></item>
/// <item><description><c>noiseRate@k</c> = |distinct top-k files on a noise path| / |distinct top-k files|; 0 when the window is empty.</description></item>
/// </list>
/// </para>
/// <para>
/// No metric embeds a "good enough" threshold. U4-0 reports raw numbers only; the acceptance line
/// ("毫秒级" / p95) is a user decision and belongs in the report, not in this class.
/// </para>
/// </summary>
public static class RetrievalMetrics
{
    /// <summary>Small k used for the "top hit is the answer" measurement.</summary>
    public const int K1 = 1;

    /// <summary>Mid k used for the headline recall number.</summary>
    public const int K5 = 5;

    /// <summary>Large k; the evaluation always requests at least this many results.</summary>
    public const int K10 = 10;

    /// <summary>Fraction of distinct expected files found within the top <paramref name="k"/> hits.</summary>
    public static double RecallAtK(
        IReadOnlyList<SearchProbeHit> rankedHits,
        IReadOnlyList<string> expectedHits,
        int k)
    {
        RequirePositiveK(k);

        var expected = PathIdentity.DistinctExpected(expectedHits);
        if (expected.Count == 0)
            return 0d;

        var window = Window(rankedHits, k);
        var matched = expected.Count(e => window.Any(h => PathIdentity.MatchesHit(h, e)));

        return (double)matched / expected.Count;
    }

    /// <summary>Reciprocal rank of the first hit matching any expected file (0 when nothing matches).</summary>
    public static double MeanReciprocalRank(
        IReadOnlyList<SearchProbeHit> rankedHits,
        IReadOnlyList<string> expectedHits)
    {
        var expected = PathIdentity.DistinctExpected(expectedHits);
        if (expected.Count == 0)
            return 0d;

        var canonical = PathIdentity.DistinctByFile(rankedHits);
        var bestRank = int.MaxValue;

        foreach (var expectation in expected)
        {
            var rank = PathIdentity.FirstRankOf(canonical, expectation);
            if (rank >= 0 && rank < bestRank)
                bestRank = rank;
        }

        return bestRank == int.MaxValue ? 0d : 1d / (bestRank + 1);
    }

    /// <summary>Fraction of the top-<paramref name="k"/> slots that match an expected file.</summary>
    public static double PrecisionAtK(
        IReadOnlyList<SearchProbeHit> rankedHits,
        IReadOnlyList<string> expectedHits,
        int k)
    {
        RequirePositiveK(k);

        var expected = PathIdentity.DistinctExpected(expectedHits);
        if (expected.Count == 0)
            return 0d;

        var window = Window(rankedHits, k);
        var relevant = window.Count(h => expected.Any(e => PathIdentity.MatchesHit(h, e)));

        return (double)relevant / k;
    }

    /// <summary>Fraction of the distinct files returned in the top <paramref name="k"/> that live on a noise path.</summary>
    public static double NoiseRateAtK(IReadOnlyList<SearchProbeHit> rankedHits, int k)
    {
        RequirePositiveK(k);

        var window = Window(rankedHits, k);
        if (window.Count == 0)
            return 0d;

        var noise = NoiseDirectoryRules.CountNoiseHits(window.Select(h => h.Path));

        return (double)noise / window.Count;
    }

    /// <summary>Raw noise-hit count inside the top <paramref name="k"/> — the numerator of <see cref="NoiseRateAtK"/>.</summary>
    public static int NoiseHitsAtK(IReadOnlyList<SearchProbeHit> rankedHits, int k)
    {
        RequirePositiveK(k);

        return NoiseDirectoryRules.CountNoiseHits(Window(rankedHits, k).Select(h => h.Path));
    }

    /// <summary>Number of distinct files inside the top <paramref name="k"/> — the denominator of <see cref="NoiseRateAtK"/>.</summary>
    public static int DistinctHitsAtK(IReadOnlyList<SearchProbeHit> rankedHits, int k)
    {
        RequirePositiveK(k);

        return Window(rankedHits, k).Count;
    }

    /// <summary>Number of top-<paramref name="k"/> files that match an expected file — numerator of <see cref="PrecisionAtK"/>.</summary>
    public static int RelevantHitsAtK(
        IReadOnlyList<SearchProbeHit> rankedHits,
        IReadOnlyList<string> expectedHits,
        int k)
    {
        RequirePositiveK(k);

        var expected = PathIdentity.DistinctExpected(expectedHits);
        if (expected.Count == 0)
            return 0;

        return Window(rankedHits, k).Count(h => expected.Any(e => PathIdentity.MatchesHit(h, e)));
    }

    private static IReadOnlyList<SearchProbeHit> Window(IReadOnlyList<SearchProbeHit> rankedHits, int k)
    {
        var canonical = PathIdentity.DistinctByFile(rankedHits);
        if (canonical.Count <= k)
            return canonical;

        return canonical.Take(k).ToArray();
    }

    private static void RequirePositiveK(int k)
    {
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be a positive integer");
    }
}
