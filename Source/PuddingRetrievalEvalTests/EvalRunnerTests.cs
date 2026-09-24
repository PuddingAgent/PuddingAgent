using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalTests;

/// <summary>
/// Runner behaviour driven by the substitute probe: metric aggregation, the cold/warm call schedule,
/// determinism detection and the fail-closed argument guards. No engine is involved.
/// </summary>
[TestClass]
public sealed class EvalRunnerTests
{
    private const double Tolerance = 1e-9;

    private static EvalSet Set(params EvalCase[] cases) => new("unit-set", 1, cases);

    private static EvalCase Case(
        string query,
        string[] expected,
        EvalKind kind = EvalKind.Symbol,
        EvalLanguage language = EvalLanguage.CSharp) =>
        new(query, expected, kind, language);

    private static EvalRunOptions Options(
        int maxResults = 10,
        int warmup = 0,
        int measured = 1,
        string scopeLabel = "unit-scope") =>
        new(new SearchScope("E:/repo"), scopeLabel, maxResults, warmup, measured);

    [TestMethod]
    public async Task RunAsync_ScoresProbeHitsAgainstExpectations()
    {
        var probe = new StubSearchProbe().AlwaysHits("repo/src/Alpha.cs", "repo/src/Beta.cs");
        var set = Set(Case("Alpha", ["repo/src/Alpha.cs"]));

        var run = await new EvalRunner(probe).RunAsync(set, Options());

        Assert.AreEqual(1d, run.RecallAt1, Tolerance);
        Assert.AreEqual(1d, run.Mrr, Tolerance);
        Assert.AreEqual(0d, run.NoiseRateAt10, Tolerance);
        Assert.AreEqual(1, run.CaseCount);
        Assert.AreEqual(0, run.FailedCaseCount);
        Assert.IsTrue(run.AllRepetitionsIdentical);
    }

    [TestMethod]
    public async Task RunAsync_AllMissesProduceZeroMetrics()
    {
        var probe = new StubSearchProbe().AlwaysHits("repo/src/Nowhere.cs");
        var set = Set(Case("Alpha", ["repo/src/Alpha.cs"]));

        var run = await new EvalRunner(probe).RunAsync(set, Options());

        Assert.AreEqual(0d, run.RecallAt1, Tolerance);
        Assert.AreEqual(0d, run.RecallAt5, Tolerance);
        Assert.AreEqual(0d, run.RecallAt10, Tolerance);
        Assert.AreEqual(0d, run.Mrr, Tolerance);
        Assert.AreEqual(0d, run.PrecisionAt5, Tolerance);
        Assert.AreEqual(0d, run.PrecisionAt10, Tolerance);
    }

    [TestMethod]
    public async Task RunAsync_NoiseRateFollowsTheProbeHits()
    {
        var probe = new StubSearchProbe().AlwaysHits("repo/node_modules/pkg/index.js");
        var set = Set(Case("anything", ["repo/src/Alpha.cs"]));

        var run = await new EvalRunner(probe).RunAsync(set, Options());

        Assert.AreEqual(1d, run.NoiseRateAt10, Tolerance);
        Assert.AreEqual(1, run.Cases[0].NoiseHitsAt10);
        Assert.AreEqual(1, run.Cases[0].DistinctHitsAt10);
    }

    [TestMethod]
    public async Task RunAsync_SeparatesColdAndWarmLatencySamples()
    {
        var probe = new StubSearchProbe().AlwaysHits("repo/src/Alpha.cs");
        var set = Set(
            Case("Alpha", ["repo/src/Alpha.cs"]),
            Case("Beta", ["repo/src/Beta.cs"]));

        var run = await new EvalRunner(probe).RunAsync(set, Options(warmup: 1, measured: 3));

        // 2 cases * (1 cold) = 2 cold samples ; 2 cases * (1 warm-up + 3 measured) = 8 warm samples
        Assert.AreEqual(2, run.ColdLatency.Count);
        Assert.AreEqual(8, run.WarmLatency.Count);
        Assert.AreEqual(2, run.ColdObservations.Count);
        Assert.IsTrue(run.ColdObservations.All(o => o.Cold));
        Assert.AreEqual(2 * (1 + 1 + 3), probe.CallCount);
    }

    [TestMethod]
    public async Task RunAsync_ScoresTheFirstMeasuredRepetition_NotTheColdOrWarmUpCall()
    {
        // call#0 (cold) misses, call#1 (warm-up) misses, call#2 (measured) hits.
        var probe = new StubSearchProbe()
            .Sequence(
                TestHits.Of("repo/src/Nowhere.cs"),
                TestHits.Of("repo/src/Nowhere.cs"),
                TestHits.Of("repo/src/Alpha.cs"));
        var set = Set(Case("Alpha", ["repo/src/Alpha.cs"]));

        var run = await new EvalRunner(probe).RunAsync(set, Options(warmup: 1, measured: 1));

        Assert.AreEqual(1d, run.RecallAt1, Tolerance, "the scored call must be the first measured one");
        Assert.AreEqual(3, probe.CallCount);
    }

    [TestMethod]
    public async Task RunAsync_DetectsNonDeterministicRepetitions()
    {
        var probe = new StubSearchProbe()
            .Sequence(
                TestHits.Of("repo/src/Alpha.cs"),
                TestHits.Of("repo/src/Alpha.cs"),
                TestHits.Of("repo/src/Beta.cs"));
        var set = Set(Case("Alpha", ["repo/src/Alpha.cs"]));

        var run = await new EvalRunner(probe).RunAsync(set, Options(warmup: 0, measured: 2));

        Assert.IsFalse(run.Cases[0].RepetitionsIdentical,
            "different measured repetitions must be reported, so accuracy numbers can be distrusted");
        Assert.IsFalse(run.AllRepetitionsIdentical);
    }

    [TestMethod]
    public async Task RunAsync_FailedProbeCallIsCountedAndScoresZero()
    {
        var probe = new StubSearchProbe { Success = false, ElapsedMs = 3 };
        var set = Set(Case("Alpha", ["repo/src/Alpha.cs"]));

        var run = await new EvalRunner(probe).RunAsync(set, Options());

        Assert.AreEqual(1, run.FailedCaseCount);
        Assert.IsFalse(run.Cases[0].Success);
        Assert.AreEqual("stub failure", run.Cases[0].Error);
        Assert.AreEqual(0d, run.RecallAt1, Tolerance);
    }

    [TestMethod]
    public async Task RunAsync_BuildsOneSlicePerLanguage()
    {
        var probe = new StubSearchProbe()
            .ForQuery("cs", "repo/src/Alpha.cs")
            .ForQuery("ts", "repo/src/app.ts")
            .ForQuery("md", "repo/docs/readme.md");
        var set = Set(
            Case("cs", ["repo/src/Alpha.cs"], EvalKind.Symbol, EvalLanguage.CSharp),
            Case("ts", ["repo/src/app.ts"], EvalKind.Symbol, EvalLanguage.TypeScript),
            Case("md", ["repo/docs/readme.md"], EvalKind.Intent, EvalLanguage.Markdown));

        var run = await new EvalRunner(probe).RunAsync(set, Options());

        Assert.AreEqual(3, run.Slices.Count);
        CollectionAssert.AreEqual(
            new[] { EvalLanguage.CSharp, EvalLanguage.TypeScript, EvalLanguage.Markdown },
            run.Slices.Select(s => s.Language).ToArray());
        Assert.IsTrue(run.Slices.All(s => s.CaseCount == 1));
        Assert.IsTrue(run.Slices.All(s => Math.Abs(s.RecallAt1 - 1d) < Tolerance));
        Assert.AreEqual("unit-scope", run.ScopeLabel);
        Assert.AreEqual("stub-probe", run.ProbeName);
        Assert.AreEqual("unit-set", run.SetName);
    }

    [TestMethod]
    public async Task RunAsync_RejectsEmptyQuerySet()
    {
        var probe = new StubSearchProbe();
        var runner = new EvalRunner(probe);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(Set(), Options()));
    }

    [TestMethod]
    public async Task RunAsync_RejectsMaxResultsBelowTen()
    {
        var runner = new EvalRunner(new StubSearchProbe());
        var set = Set(Case("Alpha", ["repo/src/Alpha.cs"]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(set, Options(maxResults: 9)));
    }

    [TestMethod]
    public async Task RunAsync_RejectsNegativeWarmup()
    {
        var runner = new EvalRunner(new StubSearchProbe());
        var set = Set(Case("Alpha", ["repo/src/Alpha.cs"]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(set, Options(warmup: -1)));
    }

    [TestMethod]
    public async Task RunAsync_IsDeterministicForASubstituteProbe()
    {
        var set = Set(
            Case("Alpha", ["repo/src/Alpha.cs"]),
            Case("Beta", ["repo/src/Beta.cs"]));

        var first = await new EvalRunner(new StubSearchProbe().AlwaysHits("repo/src/Alpha.cs"))
            .RunAsync(set, Options());
        var second = await new EvalRunner(new StubSearchProbe().AlwaysHits("repo/src/Alpha.cs"))
            .RunAsync(set, Options());

        Assert.AreEqual(first.RecallAt10, second.RecallAt10, Tolerance);
        Assert.AreEqual(first.Mrr, second.Mrr, Tolerance);
        Assert.AreEqual(first.PrecisionAt10, second.PrecisionAt10, Tolerance);
        Assert.AreEqual(first.NoiseRateAt10, second.NoiseRateAt10, Tolerance);
    }
}
