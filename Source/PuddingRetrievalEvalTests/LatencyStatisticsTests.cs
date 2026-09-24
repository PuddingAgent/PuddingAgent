using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalTests;

/// <summary>Percentile method and empty-bucket behaviour are pinned: "p95" must mean one thing only.</summary>
[TestClass]
public sealed class LatencyStatisticsTests
{
    [TestMethod]
    public void PercentileMs_UsesNearestRank()
    {
        IReadOnlyList<double> ascending = [10d, 20d, 30d, 40d];

        Assert.AreEqual(10d, LatencyStatistics.PercentileMs(ascending, 0), 1e-9);
        Assert.AreEqual(20d, LatencyStatistics.PercentileMs(ascending, 50), 1e-9);
        Assert.AreEqual(40d, LatencyStatistics.PercentileMs(ascending, 95), 1e-9);
        Assert.AreEqual(40d, LatencyStatistics.PercentileMs(ascending, 99), 1e-9);
        Assert.AreEqual(40d, LatencyStatistics.PercentileMs(ascending, 100), 1e-9);
    }

    [TestMethod]
    public void PercentileMs_OnASingleSample_IsThatSample()
    {
        IReadOnlyList<double> ascending = [7d];

        Assert.AreEqual(7d, LatencyStatistics.PercentileMs(ascending, 50), 1e-9);
        Assert.AreEqual(7d, LatencyStatistics.PercentileMs(ascending, 99), 1e-9);
    }

    [TestMethod]
    public void PercentileMs_OnTenSamples_MatchesRankArithmetic()
    {
        IReadOnlyList<double> ascending = [1d, 2d, 3d, 4d, 5d, 6d, 7d, 8d, 9d, 10d];

        // ceil(0.5*10)=5 -> index 4 ; ceil(0.95*10)=10 -> index 9 ; ceil(0.99*10)=10 -> index 9
        Assert.AreEqual(5d, LatencyStatistics.PercentileMs(ascending, 50), 1e-9);
        Assert.AreEqual(10d, LatencyStatistics.PercentileMs(ascending, 95), 1e-9);
        Assert.AreEqual(10d, LatencyStatistics.PercentileMs(ascending, 99), 1e-9);
    }

    [TestMethod]
    public void PercentileMs_KeepsSubMillisecondResolution()
    {
        IReadOnlyList<double> ascending = [0.125d, 0.5d, 1.75d];

        Assert.AreEqual(0.5d, LatencyStatistics.PercentileMs(ascending, 50), 1e-9);
        Assert.AreEqual(1.75d, LatencyStatistics.PercentileMs(ascending, 95), 1e-9);
        Assert.AreEqual(0.125d, LatencyStatistics.PercentileMs(ascending, 0), 1e-9);
    }

    [TestMethod]
    public void Summarize_ReportsRawDistribution()
    {
        var summary = LatencyStatistics.Summarize([40d, 10d, 30d, 20d]);

        Assert.AreEqual(4, summary.Count);
        Assert.AreEqual(10d, summary.MinMs, 1e-9);
        Assert.AreEqual(20d, summary.P50Ms, 1e-9);
        Assert.AreEqual(40d, summary.P95Ms, 1e-9);
        Assert.AreEqual(40d, summary.P99Ms, 1e-9);
        Assert.AreEqual(40d, summary.MaxMs, 1e-9);
        Assert.AreEqual(25d, summary.MeanMs, 1e-9);
    }

    [TestMethod]
    public void Summarize_EmptyBucketIsExplicitlyEmpty_NotFast()
    {
        var summary = LatencyStatistics.Summarize([]);

        Assert.AreEqual(0, summary.Count);
        Assert.AreEqual(0d, summary.P50Ms, 1e-9);
        Assert.AreEqual(0d, summary.P95Ms, 1e-9);
        Assert.AreEqual(0d, summary.P99Ms, 1e-9);
        Assert.AreSame(LatencySummary.Empty, summary);
    }

    [TestMethod]
    public void Summarize_IgnoresInputOrder()
    {
        var shuffled = LatencyStatistics.Summarize([30d, 10d, 20d]);
        var sorted = LatencyStatistics.Summarize([10d, 20d, 30d]);

        Assert.AreEqual(sorted.P50Ms, shuffled.P50Ms, 1e-9);
        Assert.AreEqual(sorted.P95Ms, shuffled.P95Ms, 1e-9);
    }

    [TestMethod]
    public void PercentileMs_RejectsEmptyInputAndOutOfRangePercentile()
    {
        RetrievalMetricsTests.ExpectThrows<ArgumentException>(() => LatencyStatistics.PercentileMs([], 50));
        RetrievalMetricsTests.ExpectThrows<ArgumentOutOfRangeException>(() => LatencyStatistics.PercentileMs([1d], 101));
        RetrievalMetricsTests.ExpectThrows<ArgumentOutOfRangeException>(() => LatencyStatistics.PercentileMs([1d], -1));
    }
}
