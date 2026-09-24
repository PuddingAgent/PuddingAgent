using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalTests;

/// <summary>
/// Metric correctness against known expectations (task acceptance A2). Every case here states the
/// expected number explicitly, so a wrong formula cannot pass.
/// </summary>
[TestClass]
public sealed class RetrievalMetricsTests
{
    private const double Tolerance = 1e-9;

    private static readonly IReadOnlyList<SearchProbeHit> Ranked = TestHits.Of(
        "repo/src/Alpha.cs",
        "repo/src/Beta.cs",
        "repo/src/Gamma.cs");

    [TestMethod]
    public void RecallAtK_ExpectedHitAtRankOne_IsOne()
    {
        Assert.AreEqual(1d, RetrievalMetrics.RecallAtK(Ranked, ["repo/src/Alpha.cs"], 1), Tolerance);
    }

    [TestMethod]
    public void RecallAtK_MultiExpected_IsFractionInsideWindow()
    {
        string[] expected = ["repo/src/Alpha.cs", "repo/src/Gamma.cs"];

        // window@1 = {Alpha}      -> 1 of 2
        Assert.AreEqual(0.5d, RetrievalMetrics.RecallAtK(Ranked, expected, 1), Tolerance);
        // window@2 = {Alpha,Beta} -> still 1 of 2
        Assert.AreEqual(0.5d, RetrievalMetrics.RecallAtK(Ranked, expected, 2), Tolerance);
        // window@3 = all          -> 2 of 2
        Assert.AreEqual(1d, RetrievalMetrics.RecallAtK(Ranked, expected, 3), Tolerance);
    }

    [TestMethod]
    public void RecallAtK_NoHitMatches_IsZero()
    {
        Assert.AreEqual(0d, RetrievalMetrics.RecallAtK(Ranked, ["repo/src/Missing.cs"], 10), Tolerance);
    }

    [TestMethod]
    public void RecallAtK_EmptyRankedList_IsZero()
    {
        Assert.AreEqual(0d, RetrievalMetrics.RecallAtK([], ["repo/src/Alpha.cs"], 10), Tolerance);
    }

    [TestMethod]
    public void RecallAtK_DuplicateExpectations_DoNotInflateDenominator()
    {
        // "repo/src/Alpha.cs" twice is ONE expectation, so full recall is reachable.
        var duplicated = new[] { "repo/src/Alpha.cs", "repo/src/Alpha.cs" };

        Assert.AreEqual(1d, RetrievalMetrics.RecallAtK(Ranked, duplicated, 1), Tolerance);
    }

    [TestMethod]
    public void MeanReciprocalRank_TopHitIsTheAnswer_IsOne()
    {
        Assert.AreEqual(1d, RetrievalMetrics.MeanReciprocalRank(Ranked, ["repo/src/Alpha.cs"]), Tolerance);
    }

    [TestMethod]
    public void MeanReciprocalRank_AnswerAtThirdPosition_IsOneThird()
    {
        Assert.AreEqual(1d / 3d, RetrievalMetrics.MeanReciprocalRank(Ranked, ["repo/src/Gamma.cs"]), Tolerance);
    }

    [TestMethod]
    public void MeanReciprocalRank_NoMatch_IsZero()
    {
        Assert.AreEqual(0d, RetrievalMetrics.MeanReciprocalRank(Ranked, ["repo/src/Missing.cs"]), Tolerance);
    }

    [TestMethod]
    public void MeanReciprocalRank_UsesTheBestRankAmongSeveralExpectations()
    {
        string[] expected = ["repo/src/Gamma.cs", "repo/src/Beta.cs"];

        Assert.AreEqual(0.5d, RetrievalMetrics.MeanReciprocalRank(Ranked, expected), Tolerance);
    }

    [TestMethod]
    public void PrecisionAtK_DenominatorIsK_NotHitCount()
    {
        // 1 relevant hit inside a top-5 window => 1/5, NOT 1/1.
        Assert.AreEqual(0.2d, RetrievalMetrics.PrecisionAtK(Ranked, ["repo/src/Alpha.cs"], 5), Tolerance);
        Assert.AreEqual(1d, RetrievalMetrics.PrecisionAtK(Ranked, ["repo/src/Alpha.cs"], 1), Tolerance);
    }

    [TestMethod]
    public void PrecisionAtK_AllMiss_IsZero()
    {
        Assert.AreEqual(0d, RetrievalMetrics.PrecisionAtK(Ranked, ["repo/src/Missing.cs"], 5), Tolerance);
    }

    [TestMethod]
    public void Metrics_HandleKGreaterThanHitCount()
    {
        // recall is bounded by the expectation list, precision is bounded by k.
        var oneHit = TestHits.Of("repo/src/Alpha.cs");

        Assert.AreEqual(1d, RetrievalMetrics.RecallAtK(oneHit, ["repo/src/Alpha.cs"], 10), Tolerance);
        Assert.AreEqual(0.1d, RetrievalMetrics.PrecisionAtK(oneHit, ["repo/src/Alpha.cs"], 10), Tolerance);
        Assert.AreEqual(1, RetrievalMetrics.DistinctHitsAtK(oneHit, 10));
    }

    [TestMethod]
    public void Metrics_DuplicateHitsForTheSameFile_CountOnce()
    {
        // Same file returned twice (one hit per matching line) must not eat the window.
        var lineHits = TestHits.Of(
            "repo/src/Alpha.cs",
            "repo/src/Alpha.cs",
            "repo/src/Beta.cs");

        Assert.AreEqual(1d, RetrievalMetrics.RecallAtK(lineHits, ["repo/src/Beta.cs"], 10), Tolerance);
        Assert.AreEqual(0.5d, RetrievalMetrics.MeanReciprocalRank(lineHits, ["repo/src/Beta.cs"]), Tolerance);
        Assert.AreEqual(1d / 10d, RetrievalMetrics.PrecisionAtK(lineHits, ["repo/src/Beta.cs"], 10), Tolerance);
        Assert.AreEqual(2, RetrievalMetrics.DistinctHitsAtK(lineHits, 10));
    }

    [TestMethod]
    public void NoiseRate_AllHitsInNodeModules_IsOne()
    {
        var noisy = TestHits.Of("repo/node_modules/pkg/index.js", "repo/node_modules/other/dist/x.js");

        Assert.AreEqual(1d, RetrievalMetrics.NoiseRateAtK(noisy, 10), Tolerance);
        Assert.AreEqual(2, RetrievalMetrics.NoiseHitsAtK(noisy, 10));
    }

    [TestMethod]
    public void NoiseRate_MixedHits_IsTheNoiseFraction()
    {
        var mixed = TestHits.Of(
            "repo/src/Alpha.cs",
            "repo/bin/Debug/Alpha.dll.cs",
            "repo/src/Beta.cs",
            "repo/obj/x.cs");

        Assert.AreEqual(0.5d, RetrievalMetrics.NoiseRateAtK(mixed, 10), Tolerance);
        Assert.AreEqual(2, RetrievalMetrics.NoiseHitsAtK(mixed, 10));
    }

    [TestMethod]
    public void NoiseRate_NoHits_IsZero()
    {
        Assert.AreEqual(0d, RetrievalMetrics.NoiseRateAtK([], 10), Tolerance);
    }

    [TestMethod]
    public void NoiseRate_OnlyCountsTheTopKWindow()
    {
        var hits = TestHits.Of(
            "repo/src/Alpha.cs",
            "repo/src/Beta.cs",
            "repo/publish/Alpha.js");

        Assert.AreEqual(0d, RetrievalMetrics.NoiseRateAtK(hits, 2), Tolerance);
        Assert.AreEqual(1d / 3d, RetrievalMetrics.NoiseRateAtK(hits, 3), Tolerance);
    }

    [TestMethod]
    public void Metrics_RejectNonPositiveK()
    {
        ExpectThrows<ArgumentOutOfRangeException>(() => RetrievalMetrics.RecallAtK(Ranked, ["x"], 0));
        ExpectThrows<ArgumentOutOfRangeException>(() => RetrievalMetrics.PrecisionAtK(Ranked, ["x"], -1));
        ExpectThrows<ArgumentOutOfRangeException>(() => RetrievalMetrics.NoiseRateAtK(Ranked, 0));
    }

    internal static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        var threw = false;
        try
        {
            action();
        }
        catch (TException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, $"expected {typeof(TException).Name} to be thrown");
    }
}
