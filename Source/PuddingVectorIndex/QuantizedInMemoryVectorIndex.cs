namespace PuddingVectorIndex;

/// <summary>
/// The same brute-force cosine scan as <see cref="InMemoryVectorIndex"/>, run <b>straight off the int8
/// codes</b> with a bounded top-k.
/// <para>
/// <b>Why a second type instead of a switch on the first one.</b> <see cref="InMemoryVectorIndex"/>
/// searches rows that are already float32, and the store's load path is what makes them float32
/// (<see cref="QuantizedVectorEntry.ToVectorEntry"/> dequantises every row). That is the right shape for
/// "the codes cost exactly what a float index costs in RAM", but it is the wrong shape for asking "what
/// would the scan cost if the codes were searched where they lie" — measured on three real project
/// scopes (report <c>temp/U4-3c-REPORT.md</c>), the load-time shape crosses the 50 ms end-to-end budget
/// at ~5.3k rows, i.e. before the first real scope is finished. A flag on the existing type would have
/// to change what its entries <i>are</i>; a separate type keeps both meanings intact, exactly as
/// <see cref="QuantizedVectorEntry"/> is a separate type from <see cref="VectorIndexEntry"/>.
/// </para>
/// <para>
/// <b>One similarity definition, not two.</b> The scan does not introduce a fused integer dot product.
/// For every component it performs the same arithmetic <see cref="VectorMath.Cosine"/> performs on the
/// dequantised float vector — <c>component = codes[i] * scale</c> in <see cref="float"/>, then
/// <c>sum += (double)component * query[i]</c> in <see cref="double"/> — and it multiplies the row's
/// norm with the query norm in the same order. Written as <c>(double)codes[i] * (double)scale</c> the
/// result would be a <i>second</i> similarity definition whose ranking differs from the float path, and
/// a quality difference could no longer be attributed to the storage format.
/// </para>
/// <para>
/// <b>Bounded work per query.</b> A query allocates its two heap arrays, one result array and at most
/// <c>min(topK, Count)</c> results — never one result per row, and never a full sort of the corpus.
/// </para>
/// <para>
/// <b>What is precomputed, and why.</b> Each row's norm is computed once when the row is added (one
/// <see cref="double"/> per row) instead of once per query per row. It is the <i>same</i> accumulation
/// order as <see cref="VectorMath.Norm"/> over the dequantised floats, so the scores stay bit-identical;
/// it only removes work that never depended on the query. The codes themselves are kept as they are —
/// nothing is dequantised ahead of the scan.
/// </para>
/// </summary>
public sealed class QuantizedInMemoryVectorIndex
{
    private readonly List<QuantizedVectorEntry> _entries = [];
    private readonly List<double> _norms = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <param name="dimensions">Vector length every entry must have; must be positive.</param>
    /// <param name="route">Route the codes came from, when known. Recorded so a mismatch is visible.</param>
    public QuantizedInMemoryVectorIndex(int dimensions, EmbeddingRoute? route = null)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "dimensions must be positive");

        Dimensions = dimensions;
        Route = route;
    }

    /// <summary>Vector length every entry must have.</summary>
    public int Dimensions { get; }

    /// <summary>Provider/model these codes came from; <c>null</c> when unknown.</summary>
    public EmbeddingRoute? Route { get; }

    /// <summary>Number of indexed rows.</summary>
    public int Count => _entries.Count;

    /// <summary>Rows in insertion order, still in their storage shape (codes plus scale).</summary>
    public IReadOnlyList<QuantizedVectorEntry> Entries => _entries;

    /// <summary>
    /// Adds one row, refusing exactly what <see cref="InMemoryVectorIndex.Add"/> refuses: a dimension
    /// mismatch, a duplicate id and an all-zero row. The three refusals are restated rather than
    /// delegated because this type never builds the float entry the other one inspects, but the
    /// condition is the same one — <c>codes[i] == 0</c> for every i, since a positive scale cannot turn a
    /// zero code into a non-zero component.
    /// </summary>
    public void Add(QuantizedVectorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Dimensions != Dimensions)
            throw new ArgumentException(
                $"vector dimension mismatch: the index holds {Dimensions}-dimensional vectors but entry "
                + $"'{entry.Id}' has {entry.Dimensions}; a mismatched vector must fail rather than be truncated",
                nameof(entry));

        if (!_ids.Add(entry.Id))
            throw new ArgumentException($"duplicate entry id '{entry.Id}'", nameof(entry));

        var norm = NormOf(entry.Vector);
        if (norm == 0d)
            throw new ArgumentException(
                $"entry '{entry.Id}' carries an all-zero vector, whose direction (and therefore its ranking) "
                + "would have to be invented",
                nameof(entry));

        _entries.Add(entry);
        _norms.Add(norm);
    }

    /// <summary>Adds several rows, stopping at the first violation (see <see cref="Add"/>).</summary>
    public void AddRange(IEnumerable<QuantizedVectorEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
            Add(entry);
    }

    /// <summary>
    /// Returns the <paramref name="topK"/> best rows by cosine similarity, best first — the same order
    /// <see cref="InMemoryVectorIndex.Search"/> returns for the same rows dequantised.
    /// <para>
    /// An empty index yields an empty list (a real measurement, not an error). A query whose length or
    /// norm is unusable is refused by the same rules as the float path, and both refusals are decided in
    /// <see cref="VectorMath"/> so there stays exactly one place where "vectors of different lengths are
    /// not comparable" and "a zero vector has no direction" are defined.
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

        var queryNorm = VectorMath.Norm(query);
        if (queryNorm == 0d)
            throw new InvalidOperationException("the query vector is all zeros, so no similarity can be computed");

        if (_entries.Count == 0)
            return [];

        // Bounded selection: a max-heap on the shipped order, so its root is the weakest survivor and a
        // row that cannot beat the root can be dropped after one comparison.
        var capacity = Math.Min(topK, _entries.Count);
        var heapScores = new double[capacity];
        var heapRows = new int[capacity];
        var kept = 0;

        for (var row = 0; row < _entries.Count; row++)
        {
            var score = Score(row, query, queryNorm);

            if (kept < capacity)
            {
                heapScores[kept] = score;
                heapRows[kept] = row;
                kept++;
                SiftUp(heapScores, heapRows, kept - 1);
                continue;
            }

            // `>= 0` is "does not rank strictly before the weakest survivor". Ids are unique, so the
            // order is total and this never drops a row that would have displaced the root.
            if (RankOrder(row, score, heapRows[0], heapScores[0]) >= 0)
                continue;

            heapScores[0] = score;
            heapRows[0] = row;
            SiftDown(heapScores, heapRows, 0, kept);
        }

        // Popping the weakest first means the last pop is rank 0, so no sort of the winners is needed.
        var results = new VectorSearchResult[kept];
        var remaining = kept;
        for (var rank = results.Length - 1; rank >= 0; rank--)
        {
            var entry = _entries[heapRows[0]];
            results[rank] = new VectorSearchResult(
                entry.Id,
                entry.SourceFile,
                entry.StartLine,
                entry.EndLine,
                entry.Kind,
                heapScores[0],
                rank);

            remaining--;
            if (remaining == 0)
                break;

            heapScores[0] = heapScores[remaining];
            heapRows[0] = heapRows[remaining];
            SiftDown(heapScores, heapRows, 0, remaining);
        }

        return results;
    }

    /// <summary>
    /// Cosine similarity of one row against the query, accumulated in the exact order
    /// <see cref="VectorMath.Cosine"/> uses (<c>Dot / (Norm(row) * Norm(query))</c>).
    /// </summary>
    private double Score(int row, ReadOnlySpan<float> query, double queryNorm)
    {
        var vector = _entries[row].Vector;
        var codes = vector.Span;
        var scale = vector.Scale;

        double dot = 0d;
        for (var i = 0; i < codes.Length; i++)
        {
            // The scale must land in float first: `codes[i] * scale` is the same float the dequantiser
            // produces, while `(double)codes[i] * (double)scale` would accumulate a different value.
            var component = codes[i] * scale;
            dot += (double)component * query[i];
        }

        return dot / (_norms[row] * queryNorm);
    }

    /// <summary>
    /// L2 norm of a row, accumulated in the same order as <see cref="VectorMath.Norm"/> applied to that
    /// row's dequantised floats.
    /// </summary>
    private static double NormOf(QuantizedVector vector)
    {
        var codes = vector.Span;
        var scale = vector.Scale;

        double sum = 0d;
        for (var i = 0; i < codes.Length; i++)
        {
            var component = codes[i] * scale;
            sum += (double)component * component;
        }

        return Math.Sqrt(sum);
    }

    /// <summary>
    /// The shipped total order as a comparison of two row indexes: negative when <paramref name="leftRow"/>
    /// ranks first. It is <see cref="InMemoryVectorIndex.Search"/>'s sort comparator verbatim — score
    /// descending, ties broken by <see cref="QuantizedVectorEntry.Id"/> in ordinal order — so a window
    /// chosen by the heap is the same window a full sort would produce.
    /// </summary>
    private int RankOrder(int leftRow, double leftScore, int rightRow, double rightScore)
    {
        var byScore = rightScore.CompareTo(leftScore);
        return byScore != 0 ? byScore : string.CompareOrdinal(_entries[leftRow].Id, _entries[rightRow].Id);
    }

    /// <summary>Restores the heap property upwards: the weakest element stays at the root.</summary>
    private void SiftUp(double[] scores, int[] rows, int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (RankOrder(rows[index], scores[index], rows[parent], scores[parent]) <= 0)
                return;

            (scores[index], scores[parent]) = (scores[parent], scores[index]);
            (rows[index], rows[parent]) = (rows[parent], rows[index]);
            index = parent;
        }
    }

    /// <summary>Restores the heap property downwards from <paramref name="index"/> over <paramref name="count"/> elements.</summary>
    private void SiftDown(double[] scores, int[] rows, int index, int count)
    {
        while (true)
        {
            var left = (2 * index) + 1;
            if (left >= count)
                return;

            var weakest = left;
            var right = left + 1;
            if (right < count && RankOrder(rows[right], scores[right], rows[left], scores[left]) > 0)
                weakest = right;

            if (RankOrder(rows[weakest], scores[weakest], rows[index], scores[index]) <= 0)
                return;

            (scores[index], scores[weakest]) = (scores[weakest], scores[index]);
            (rows[index], rows[weakest]) = (rows[weakest], rows[index]);
            index = weakest;
        }
    }
}
