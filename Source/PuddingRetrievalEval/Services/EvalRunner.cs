using System.Diagnostics;
using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEval.Services;

/// <summary>How a run is executed. Cold and warm samples are always kept apart.</summary>
/// <param name="Scope">The scope handed to every probe call.</param>
/// <param name="ScopeLabel">Human label for the scope, used in reports (e.g. <c>repo-root</c>).</param>
/// <param name="MaxResults">Results requested per call; must be &gt;= 10 so <c>recall@10</c> is measurable.</param>
/// <param name="WarmupRepetitions">
/// Extra calls after the cold one and before the measured ones, feeding the warm latency bucket only.
/// Default <c>1</c>: it removes the first-search penalty (reader/page-cache warm-up) from the measured
/// warm latency without discarding the cold measurement.
/// </param>
/// <param name="MeasuredRepetitions">
/// Scored-then-timed repetitions. Accuracy is scored on the <b>first</b> measured repetition; every
/// measured repetition contributes to the warm latency bucket and to the determinism check.
/// </param>
public sealed record EvalRunOptions(
    SearchScope Scope,
    string ScopeLabel,
    int MaxResults = 20,
    int WarmupRepetitions = 1,
    int MeasuredRepetitions = 3);

/// <summary>
/// Drives one query set against one <see cref="ISearchProbe"/> and produces one <see cref="EvalRun"/>.
/// <para>
/// Per case the call sequence is: <b>1 cold call</b> → <c>WarmupRepetitions</c> warm-up calls →
/// <c>MeasuredRepetitions</c> measured calls. The cold call is timed but never scored (it exists to
/// quantify the cold penalty); the first measured call is scored; all warm calls are timed.
/// "Cold" here therefore means "first call for this query in this process", not "process freshly
/// started" — see the baseline report's RISKS section for what that does and does not cover.
/// </para>
/// <para>No thresholds: this class produces numbers, never verdicts.</para>
/// </summary>
public sealed class EvalRunner
{
    private readonly ISearchProbe _probe;

    public EvalRunner(ISearchProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<EvalRun> RunAsync(
        EvalSet set,
        EvalRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(options);

        // A run over zero cases would silently emit all-zero aggregates; refuse instead (fail-closed).
        if (set.Cases.Count == 0)
            throw new ArgumentException("query set contains no cases", nameof(set));

        if (options.Scope is null)
            throw new ArgumentNullException(nameof(options), "options.Scope is required");

        if (options.MaxResults < RetrievalMetrics.K10)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxResults, $"MaxResults must be >= {RetrievalMetrics.K10}");

        if (options.WarmupRepetitions < 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.WarmupRepetitions, "WarmupRepetitions must be >= 0");

        if (options.MeasuredRepetitions < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.MeasuredRepetitions, "MeasuredRepetitions must be >= 1");

        var startedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        var started = Stopwatch.StartNew();

        var coldSamples = new List<double>();
        var warmSamples = new List<double>();
        var coldObservations = new List<EvalObservation>();
        var scores = new List<EvalCaseScore>();
        var callIndex = 0;

        foreach (var evalCase in set.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new SearchProbeRequest(evalCase.Query, options.Scope, options.MaxResults);

            var cold = await _probe.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            coldSamples.Add(cold.ElapsedMs);
            coldObservations.Add(new EvalObservation(
                callIndex++, true, cold.Hits?.Count ?? 0, cold.ElapsedMs, cold.Success, cold.Error));

            var measured = new List<SearchProbeOutcome>();
            var totalWarmCalls = options.WarmupRepetitions + options.MeasuredRepetitions;

            for (var i = 0; i < totalWarmCalls; i++)
            {
                var outcome = await _probe.SearchAsync(request, cancellationToken).ConfigureAwait(false);
                warmSamples.Add(outcome.ElapsedMs);

                if (i >= options.WarmupRepetitions)
                    measured.Add(outcome);
            }

            scores.Add(Score(evalCase, measured));
        }

        var expected = set.Cases.Count;

        return new EvalRun(
            ProbeName: _probe.Name,
            SetName: set.Name,
            SetVersion: set.Version,
            ScopeLabel: options.ScopeLabel,
            ScopeRootDirectory: options.Scope.RootDirectory,
            StartedAtUtc: startedAtUtc,
            TotalElapsedMs: started.ElapsedMilliseconds,
            CaseCount: expected,
            FailedCaseCount: scores.Count(s => !s.Success),
            RecallAt1: Mean(scores, s => s.RecallAt1),
            RecallAt5: Mean(scores, s => s.RecallAt5),
            RecallAt10: Mean(scores, s => s.RecallAt10),
            Mrr: Mean(scores, s => s.Mrr),
            PrecisionAt5: Mean(scores, s => s.PrecisionAt5),
            PrecisionAt10: Mean(scores, s => s.PrecisionAt10),
            NoiseRateAt10: Mean(scores, s => s.NoiseRateAt10),
            Slices: BuildSlices(scores),
            ColdLatency: LatencyStatistics.Summarize(coldSamples),
            WarmLatency: LatencyStatistics.Summarize(warmSamples),
            AllRepetitionsIdentical: scores.All(s => s.RepetitionsIdentical),
            ColdObservations: coldObservations,
            Cases: scores);
    }

    private static EvalCaseScore Score(EvalCase evalCase, IReadOnlyList<SearchProbeOutcome> measured)
    {
        // MeasuredRepetitions >= 1 is enforced by the caller, so index 0 always exists.
        var scored = measured[0];
        var ranked = scored.Hits ?? [];

        var signature = Signature(ranked);
        var identical = measured.All(o => Signature(o.Hits ?? []).SequenceEqual(signature, StringComparer.Ordinal));

        var expected = PathIdentity.DistinctExpected(evalCase.ExpectedHits);
        var canonical = PathIdentity.DistinctByFile(ranked);
        var matched = expected.Count(e => canonical.Any(h => PathIdentity.MatchesHit(h, e)));

        return new EvalCaseScore(
            Case: evalCase,
            RankedHits: canonical,
            ElapsedMs: scored.ElapsedMs,
            Success: scored.Success,
            Error: scored.Error,
            RecallAt1: RetrievalMetrics.RecallAtK(ranked, evalCase.ExpectedHits, RetrievalMetrics.K1),
            RecallAt5: RetrievalMetrics.RecallAtK(ranked, evalCase.ExpectedHits, RetrievalMetrics.K5),
            RecallAt10: RetrievalMetrics.RecallAtK(ranked, evalCase.ExpectedHits, RetrievalMetrics.K10),
            Mrr: RetrievalMetrics.MeanReciprocalRank(ranked, evalCase.ExpectedHits),
            PrecisionAt5: RetrievalMetrics.PrecisionAtK(ranked, evalCase.ExpectedHits, RetrievalMetrics.K5),
            PrecisionAt10: RetrievalMetrics.PrecisionAtK(ranked, evalCase.ExpectedHits, RetrievalMetrics.K10),
            NoiseRateAt10: RetrievalMetrics.NoiseRateAtK(ranked, RetrievalMetrics.K10),
            NoiseHitsAt10: RetrievalMetrics.NoiseHitsAtK(ranked, RetrievalMetrics.K10),
            DistinctHitsAt10: RetrievalMetrics.DistinctHitsAtK(ranked, RetrievalMetrics.K10),
            RelevantHitsAt10: RetrievalMetrics.RelevantHitsAtK(ranked, evalCase.ExpectedHits, RetrievalMetrics.K10),
            MatchedExpectedCount: matched,
            ExpectedCount: expected.Count,
            RepetitionsIdentical: identical);
    }

    private static string[] Signature(IReadOnlyList<SearchProbeHit> hits) =>
        PathIdentity.DistinctByFile(hits).Select(h => PathIdentity.Normalize(h.Path)).ToArray();

    private static IReadOnlyList<EvalSlice> BuildSlices(IReadOnlyList<EvalCaseScore> scores) =>
        scores
            .Select(s => s.Case.Language)
            .Distinct()
            .OrderBy(language => (int)language)
            .Select(language =>
            {
                var group = scores.Where(s => s.Case.Language == language).ToArray();
                return new EvalSlice(
                    Language: language,
                    CaseCount: group.Length,
                    RecallAt1: Mean(group, s => s.RecallAt1),
                    RecallAt5: Mean(group, s => s.RecallAt5),
                    RecallAt10: Mean(group, s => s.RecallAt10),
                    Mrr: Mean(group, s => s.Mrr),
                    PrecisionAt5: Mean(group, s => s.PrecisionAt5),
                    PrecisionAt10: Mean(group, s => s.PrecisionAt10),
                    NoiseRateAt10: Mean(group, s => s.NoiseRateAt10));
            })
            .ToArray();

    /// <summary>Mean of a metric over a group; rounded to 4 decimals so report diffs stay stable.</summary>
    private static double Mean(IReadOnlyList<EvalCaseScore> group, Func<EvalCaseScore, double> selector)
        => group.Count == 0 ? 0d : Math.Round(group.Average(selector), 4);
}
