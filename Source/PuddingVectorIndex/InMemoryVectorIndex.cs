namespace PuddingVectorIndex;

/// <summary>
/// Brute-force cosine index held in memory.
/// <para>
/// A real vector database is deliberately <b>not</b> used (ADR-089 / task book: 不引入向量数据库、不新增
/// NuGet). The measured corpus is a 37-file directory — a few thousand vectors — where an exhaustive
/// scan is faster than any index structure would be to maintain, and where an approximate index would
/// inject recall loss of its own into a comparison that is supposed to measure <i>the embedding</i>.
/// This is a boundary of the current slice, not a claim about production scale: at repository scale the
/// exhaustive scan would have to be replaced (see the U4-1b report's honesty section).
/// </para>
/// <para>
/// Ranking is fully deterministic: score descending, ties broken by <see cref="VectorIndexEntry.Id"/>
/// in ordinal order. Without the tie-break, two runs over equal vectors could return different top-k
/// windows and the "repetition-stable" check in the evaluation would fail for reasons that have
/// nothing to do with retrieval.
/// </para>
/// </summary>
public sealed class InMemoryVectorIndex
{
    private readonly List<VectorIndexEntry> _entries = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <param name="dimensions">Vector length every entry must have; must be positive.</param>
    /// <param name="route">Route the vectors came from, when known. Recorded so a mismatch is visible.</param>
    public InMemoryVectorIndex(int dimensions, EmbeddingRoute? route = null)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "dimensions must be positive");

        Dimensions = dimensions;
        Route = route;
    }

    /// <summary>Vector length every entry must have.</summary>
    public int Dimensions { get; }

    /// <summary>Provider/model these vectors came from; <c>null</c> when unknown.</summary>
    public EmbeddingRoute? Route { get; }

    /// <summary>Number of indexed vectors.</summary>
    public int Count => _entries.Count;

    /// <summary>Entries in insertion order (the order the store was written in).</summary>
    public IReadOnlyList<VectorIndexEntry> Entries => _entries;

    /// <summary>
    /// Adds one entry. Fail-closed on a dimension mismatch, a duplicate id and a zero vector — all
    /// three would otherwise corrupt every subsequent ranking while still looking like a working index.
    /// </summary>
    public void Add(VectorIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Dimensions != Dimensions)
            throw new ArgumentException(
                $"vector dimension mismatch: the index holds {Dimensions}-dimensional vectors but entry "
                + $"'{entry.Id}' has {entry.Dimensions}; a mismatched vector must fail rather than be truncated",
                nameof(entry));

        if (!_ids.Add(entry.Id))
            throw new ArgumentException($"duplicate entry id '{entry.Id}'", nameof(entry));

        if (VectorMath.Norm(entry.Span) == 0d)
            throw new ArgumentException(
                $"entry '{entry.Id}' carries an all-zero vector, whose direction (and therefore its ranking) "
                + "would have to be invented",
                nameof(entry));

        _entries.Add(entry);
    }

    /// <summary>Adds several entries, stopping at the first violation (see <see cref="Add"/>).</summary>
    public void AddRange(IEnumerable<VectorIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
            Add(entry);
    }

    /// <summary>
    /// Returns the <paramref name="topK"/> best entries by cosine similarity, best first.
    /// <para>
    /// An empty index yields an empty list (a real measurement, not an error). A query whose length or
    /// norm is unusable is an error — see <see cref="VectorMath.Cosine"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<VectorSearchResult> Search(ReadOnlySpan<float> query, int topK)
    {
        if (query.Length != Dimensions)
            throw new ArgumentException(
                $"query dimension mismatch: the index holds {Dimensions}-dimensional vectors but the query "
                + $"has {query.Length}",
                nameof(query));

        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "topK must be a positive integer");

        if (VectorMath.Norm(query) == 0d)
            throw new InvalidOperationException("the query vector is all zeros, so no similarity can be computed");

        var scored = new List<VectorSearchResult>(_entries.Count);
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            scored.Add(new VectorSearchResult(
                entry.Id,
                entry.SourceFile,
                entry.StartLine,
                entry.EndLine,
                entry.Kind,
                VectorMath.Cosine(entry.Span, query),
                Rank: 0));
        }

        scored.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.Id, right.Id);
        });

        var take = Math.Min(topK, scored.Count);
        var results = new List<VectorSearchResult>(take);
        for (var rank = 0; rank < take; rank++)
            results.Add(scored[rank] with { Rank = rank });

        return results;
    }
}
