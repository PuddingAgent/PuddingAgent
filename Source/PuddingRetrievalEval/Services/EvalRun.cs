using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEval.Services;

/// <summary>One raw probe call, recorded verbatim so a reviewer can re-derive every number.</summary>
public sealed record EvalObservation(
    int Index,
    bool Cold,
    int HitCount,
    double ElapsedMs,
    bool Success,
    string? Error);

/// <summary>Score of one case, computed from its first <i>measured</i> (non-warm-up) repetition.</summary>
public sealed record EvalCaseScore(
    EvalCase Case,
    IReadOnlyList<SearchProbeHit> RankedHits,
    double ElapsedMs,
    bool Success,
    string? Error,
    double RecallAt1,
    double RecallAt5,
    double RecallAt10,
    double Mrr,
    double PrecisionAt5,
    double PrecisionAt10,
    double NoiseRateAt10,
    int NoiseHitsAt10,
    int DistinctHitsAt10,
    int RelevantHitsAt10,
    int MatchedExpectedCount,
    int ExpectedCount,
    bool RepetitionsIdentical);

/// <summary>Aggregated metrics for one language stratum.</summary>
public sealed record EvalSlice(
    EvalLanguage Language,
    int CaseCount,
    double RecallAt1,
    double RecallAt5,
    double RecallAt10,
    double Mrr,
    double PrecisionAt5,
    double PrecisionAt10,
    double NoiseRateAt10);

/// <summary>The complete result of one evaluation run: raw case scores, aggregates and latency buckets.</summary>
public sealed record EvalRun(
    string ProbeName,
    string SetName,
    int SetVersion,
    string ScopeLabel,
    string ScopeRootDirectory,
    string StartedAtUtc,
    long TotalElapsedMs,
    int CaseCount,
    int FailedCaseCount,
    double RecallAt1,
    double RecallAt5,
    double RecallAt10,
    double Mrr,
    double PrecisionAt5,
    double PrecisionAt10,
    double NoiseRateAt10,
    IReadOnlyList<EvalSlice> Slices,
    LatencySummary ColdLatency,
    LatencySummary WarmLatency,
    bool AllRepetitionsIdentical,
    IReadOnlyList<EvalObservation> ColdObservations,
    IReadOnlyList<EvalCaseScore> Cases);
