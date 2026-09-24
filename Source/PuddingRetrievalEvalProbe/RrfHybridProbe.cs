using System.Diagnostics;
using System.Globalization;
using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Hybrid retriever: Reciprocal Rank Fusion of the full-text ranking and the vector ranking.
/// <para>
/// <b>Fusion is at file level, not chunk level.</b> The evaluation scores files, and the two sides
/// produce different hit granularities anyway (Lucene returns one hit per matching line, the vector
/// index one per chunk). Fusing at file level also stops a chatty file from filling its own side's
/// ranked list, which is the same canonicalisation the metrics apply — so the fused ranking and the
/// measured ranking agree on what a "hit" is.
/// </para>
/// <para>
/// <b>Parameters</b> (all recorded in the report, all overridable from the command line):
/// <c>k</c> = the RRF constant added to each rank; <c>wFullText</c>/<c>wVector</c> = per-side weights;
/// <c>fusionDepth</c> = how many hits each side is asked for before fusing (must exceed the K10 window
/// the metrics look at, otherwise a file ranked 11th by one side could never contribute).
/// </para>
/// <para>
/// The score is <c>sum over sides of weight / (k + rank)</c> with 1-based ranks; a file missing from one
/// side simply gets no term from it. Ties break on the normalised path, so a run is reproducible.
/// </para>
/// </summary>
internal sealed class RrfHybridProbe : ISearchProbe
{
    private readonly ISearchProbe _fullText;
    private readonly ISearchProbe _vector;
    private readonly int _k;
    private readonly double _weightFullText;
    private readonly double _weightVector;
    private readonly int _fusionDepth;

    public RrfHybridProbe(
        ISearchProbe fullText,
        ISearchProbe vector,
        int k,
        double weightFullText,
        double weightVector,
        int fusionDepth,
        string name)
    {
        _fullText = fullText ?? throw new ArgumentNullException(nameof(fullText));
        _vector = vector ?? throw new ArgumentNullException(nameof(vector));

        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "the RRF constant must be positive");

        if (fusionDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(fusionDepth), fusionDepth, "fusion depth must be positive");

        _k = k;
        _weightFullText = weightFullText;
        _weightVector = weightVector;
        _fusionDepth = fusionDepth;
        Name = name;
    }

    public string Name { get; }

    /// <summary>The parameters as they are written into the report — one string, no hidden defaults.</summary>
    public string Parameters =>
        string.Format(
            CultureInfo.InvariantCulture,
            "rrf k={0} wFullText={1} wVector={2} fusionDepth={3}",
            _k,
            _weightFullText,
            _weightVector,
            _fusionDepth);

    public async Task<SearchProbeOutcome> SearchAsync(
        SearchProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var depth = Math.Max(request.MaxResults, _fusionDepth);
        var deep = request with { MaxResults = depth };

        var stopwatch = Stopwatch.StartNew();
        var fullText = await _fullText.SearchAsync(deep, cancellationToken).ConfigureAwait(false);
        var vector = await _vector.SearchAsync(deep, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!fullText.Success)
            return SearchProbeOutcome.Failure(stopwatch.Elapsed.TotalMilliseconds, $"full-text side failed: {fullText.Error}");

        if (!vector.Success)
            return SearchProbeOutcome.Failure(stopwatch.Elapsed.TotalMilliseconds, $"vector side failed: {vector.Error}");

        var fused = Fuse(fullText.Hits ?? [], vector.Hits ?? [], request.MaxResults);
        return new SearchProbeOutcome(fused, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>RRF over the distinct-file view of each side. Pure function, so it can be reasoned about.</summary>
    internal IReadOnlyList<SearchProbeHit> Fuse(
        IReadOnlyList<SearchProbeHit> fullTextHits,
        IReadOnlyList<SearchProbeHit> vectorHits,
        int take)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var representative = new Dictionary<string, SearchProbeHit>(StringComparer.Ordinal);

        Accumulate(fullTextHits, _weightFullText, scores, representative);
        Accumulate(vectorHits, _weightVector, scores, representative);

        return scores.Keys
            .OrderByDescending(key => scores[key])
            .ThenBy(key => key, StringComparer.Ordinal)
            .Take(take)
            .Select(key => representative[key])
            .ToArray();
    }

    private void Accumulate(
        IReadOnlyList<SearchProbeHit> hits,
        double weight,
        Dictionary<string, double> scores,
        Dictionary<string, SearchProbeHit> representative)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rank = 0;

        foreach (var hit in hits)
        {
            var key = PathIdentity.Normalize(hit.Path);
            if (key.Length == 0 || !seen.Add(key))
                continue;

            rank++;
            scores[key] = scores.GetValueOrDefault(key) + weight / (_k + rank);

            if (!representative.ContainsKey(key))
                representative[key] = hit;
        }
    }
}
