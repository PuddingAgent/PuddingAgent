using PuddingRetrievalEval.Contracts;

namespace PuddingRetrievalEvalTests;

/// <summary>
/// A hand-built substitute retrieval probe: canned ranked hits, no engine, no IO.
/// <para>
/// Every accuracy assertion in this suite is written against known expectations fed to this stub, so a
/// regression in the metric formulas cannot be hidden by an engine's behaviour.
/// </para>
/// </summary>
internal sealed class StubSearchProbe : ISearchProbe
{
    private readonly Dictionary<string, IReadOnlyList<SearchProbeHit>> _perQuery = new(StringComparer.Ordinal);
    private readonly List<IReadOnlyList<SearchProbeHit>> _sequence = [];
    private readonly List<string> _calls = [];

    public StubSearchProbe(string name = "stub-probe")
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Number of calls made so far, used to prove the cold/warm call schedule.</summary>
    public int CallCount => _calls.Count;

    /// <summary>Queries seen, in call order.</summary>
    public IReadOnlyList<string> Queries => _calls;

    /// <summary>Latency reported by every call (fractional milliseconds).</summary>
    public double ElapsedMs { get; set; } = 1d;

    /// <summary>When false every call reports failure (used for the failed-call path).</summary>
    public bool Success { get; set; } = true;

    /// <summary>Hits returned by every call whose query has no per-query answer.</summary>
    public IReadOnlyList<SearchProbeHit> Default { get; set; } = [];

    public StubSearchProbe AlwaysHits(params string[] paths)
    {
        Default = TestHits.Of(paths);
        return this;
    }

    public StubSearchProbe ForQuery(string query, params string[] paths)
    {
        _perQuery[query] = TestHits.Of(paths);
        return this;
    }

    /// <summary>Call-order scripted answers; the last entry is repeated once exhausted.</summary>
    public StubSearchProbe Sequence(params IReadOnlyList<SearchProbeHit>[] responses)
    {
        _sequence.Clear();
        _sequence.AddRange(responses);
        return this;
    }

    public Task<SearchProbeOutcome> SearchAsync(SearchProbeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var index = _calls.Count;
        _calls.Add(request.Query);

        if (!Success)
            return Task.FromResult(SearchProbeOutcome.Failure(ElapsedMs, "stub failure"));

        IReadOnlyList<SearchProbeHit> hits;
        if (_sequence.Count > 0)
            hits = _sequence[Math.Min(index, _sequence.Count - 1)];
        else if (_perQuery.TryGetValue(request.Query, out var perQuery))
            hits = perQuery;
        else
            hits = Default;

        return Task.FromResult(new SearchProbeOutcome(hits, ElapsedMs));
    }
}

internal static class TestHits
{
    public static IReadOnlyList<SearchProbeHit> Of(params string[] paths) =>
        paths.Select(path => new SearchProbeHit(path)).ToArray();

    public static IReadOnlyList<SearchProbeHit> Of(params (string Path, string Symbol)[] hits) =>
        hits.Select(hit => new SearchProbeHit(hit.Path, hit.Symbol)).ToArray();
}
