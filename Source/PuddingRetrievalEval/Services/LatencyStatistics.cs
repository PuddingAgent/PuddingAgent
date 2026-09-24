namespace PuddingRetrievalEval.Services;

/// <summary>
/// Raw latency distribution of one latency bucket (all cold samples, or all warm samples).
/// No threshold, no verdict — see <see cref="LatencyStatistics"/>.
/// </summary>
public sealed record LatencySummary(
    int Count,
    double MinMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    double MeanMs)
{
    /// <summary>The empty distribution: an absent bucket is reported as "no samples", never as "fast".</summary>
    public static LatencySummary Empty { get; } = new(0, 0d, 0d, 0d, 0d, 0d, 0d);
}

/// <summary>
/// Percentile computation for latency samples, kept deliberately simple and pinned by unit tests.
/// <para>
/// Method: <b>nearest-rank</b> — for <c>n</c> samples ascending, the p-th percentile is the element at
/// 0-based index <c>clamp(ceil(p/100 * n) - 1, 0, n-1)</c>. With <c>p = 50</c> and 4 samples
/// <c>[1,2,3,4]</c> the result is <c>2</c> (index 1). Documented because "percentile" is otherwise
/// ambiguous (a quantile-interpolation implementation would give 2.5 for the same input).
/// </para>
/// <para>
/// Samples are fractional milliseconds: local retrieval answers below the 1 ms timer resolution often
/// enough that integer milliseconds would erase the distribution.
/// </para>
/// <para>Cold and warm samples are summarised separately; U4-0 never blends them.</para>
/// </summary>
public static class LatencyStatistics
{
    /// <summary>Summarises samples. An empty sequence yields <see cref="LatencySummary.Empty"/> rather than throwing.</summary>
    public static LatencySummary Summarize(IEnumerable<double> samplesMs)
    {
        var samples = samplesMs?.OrderBy(v => v).ToArray() ?? [];
        if (samples.Length == 0)
            return LatencySummary.Empty;

        return new LatencySummary(
            Count: samples.Length,
            MinMs: Round(samples[0]),
            P50Ms: PercentileMs(samples, 50),
            P95Ms: PercentileMs(samples, 95),
            P99Ms: PercentileMs(samples, 99),
            MaxMs: Round(samples[^1]),
            MeanMs: Round(samples.Average(v => v)));
    }

    /// <summary>
    /// p-th percentile (0..100) of an <b>ascending</b> sample array using the nearest-rank method.
    /// </summary>
    public static double PercentileMs(IReadOnlyList<double> ascendingSamplesMs, double percentile)
    {
        if (ascendingSamplesMs is null || ascendingSamplesMs.Count == 0)
            throw new ArgumentException("at least one latency sample is required", nameof(ascendingSamplesMs));

        if (percentile < 0 || percentile > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "percentile must be within [0, 100]");

        var rank = (int)Math.Ceiling(percentile / 100d * ascendingSamplesMs.Count);
        var index = Math.Clamp(rank - 1, 0, ascendingSamplesMs.Count - 1);

        return Round(ascendingSamplesMs[index]);
    }

    private static double Round(double value) => Math.Round(value, 3);
}
