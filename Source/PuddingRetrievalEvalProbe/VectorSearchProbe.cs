using System.Diagnostics;
using PuddingRetrievalEval.Contracts;
using PuddingVectorIndex;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Real retrieval surface #2: the in-memory cosine index produced by <c>PuddingVectorIndex</c>.
/// <para>
/// The adapter is intentionally thin: it embeds the query through the same port the index was built
/// through, asks the index for the top-k, and times the <b>whole</b> call — embedding included, as the
/// port contract requires. Query embedding is deliberately <b>not</b> cached: caching would hide the
/// dominant cost (a local single embed is ~100-400 ms, three orders of magnitude above the cosine
/// scan) and would make the warm latency look like the full-text engine's.
/// </para>
/// <para>
/// No fallback of any kind: if the vector service fails, the call is a failure. The whole point of
/// measuring this retriever is to find out what vector-only retrieval costs and delivers; degrading to
/// full-text here would silently answer a different question.
/// </para>
/// </summary>
internal sealed class VectorSearchProbe : ISearchProbe
{
    private readonly InMemoryVectorIndex _index;
    private readonly IEmbeddingProvider _provider;

    public VectorSearchProbe(InMemoryVectorIndex index, IEmbeddingProvider provider, string name)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Name = name;
    }

    public string Name { get; }

    public async Task<SearchProbeOutcome> SearchAsync(
        SearchProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var queryVector = await _provider.EmbedAsync(request.Query, cancellationToken).ConfigureAwait(false);
            var results = _index.Search(queryVector, request.MaxResults);
            stopwatch.Stop();

            var hits = results
                .Select(result => new SearchProbeHit(result.SourceFile, null, result.StartLine))
                .ToArray();

            return new SearchProbeOutcome(hits, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return SearchProbeOutcome.Failure(
                stopwatch.Elapsed.TotalMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
