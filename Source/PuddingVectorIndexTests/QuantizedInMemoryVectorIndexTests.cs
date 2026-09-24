using PuddingVectorIndex;

namespace PuddingVectorIndexTests;

/// <summary>
/// Contract tests for the in-place int8 scan (task book U4-3c §5, assertions A1~A5).
/// <para>
/// The claim under test is not "the optimised path looks reasonable". It is that the codes can be
/// scanned where they lie and <b>still produce the shipped ranking bit for bit</b> — same ids in the
/// same order, scores that differ by exactly zero — while allocating a bounded amount per query. Each
/// of those three is asserted directly, so a mutation of the scan's arithmetic, of its tie-break or of
/// its boundedness has to turn a specific test red instead of merely looking suspicious.
/// </para>
/// <para>
/// The corpus here is generated deterministically and quantised through the component's own
/// <see cref="VectorQuantizer"/>; the real-store equivalence measurement lives in the probe
/// (<c>Source/PuddingRetrievalEvalProbe/VectorScanBench.cs</c>), because a unit test must not depend on
/// a store written under <c>temp/</c>.
/// </para>
/// </summary>
[TestClass]
public sealed class QuantizedInMemoryVectorIndexTests
{
    /// <summary>Deterministic 64-bit LCG — a corpus that does not change with the BCL's <c>Random</c> implementation.</summary>
    private struct Lcg(ulong seed)
    {
        private ulong _state = seed;

        public double NextDouble()
        {
            _state = unchecked((_state * 6364136223846793005UL) + 1442695040888963407UL);
            return (_state >> 11) * (1.0 / 9007199254740992.0);
        }
    }

    private static string IdOf(int row) => $"chunk-{row:D6}";

    private static float[] RandomVector(Lcg rng, int dimensions, int index)
    {
        var vector = new float[dimensions];
        for (var d = 0; d < dimensions; d++)
            vector[d] = (float)((rng.NextDouble() * 2) - 1);

        // A non-zero component at a deterministic position: no row may be all-zero, because such a row
        // has no direction and both paths refuse it.
        vector[index % dimensions] = 1f;
        return vector;
    }

    private static QuantizedVectorEntry Row(string id, float[] vector) =>
        new(id, "Source/Demo/Alpha.cs", 12, 20, "Outline", 2f, VectorQuantizer.Quantize(vector));

    private static (QuantizedInMemoryVectorIndex Quantized, InMemoryVectorIndex Shipped) BuildIndexes(
        int rows,
        int dimensions,
        ulong seed)
    {
        var quantized = new QuantizedInMemoryVectorIndex(dimensions);
        var shipped = new InMemoryVectorIndex(dimensions);
        var rng = new Lcg(seed);

        for (var i = 0; i < rows; i++)
        {
            var row = Row(IdOf(i), RandomVector(rng, dimensions, i));
            quantized.Add(row);

            // The shipped path is "the same store loaded today": every row dequantised into a float32
            // entry at load time, which is exactly what this slice removes from the query path.
            shipped.Add(row.ToVectorEntry());
        }

        return (quantized, shipped);
    }

    private static float[] Query(int dimensions, ulong seed, int index)
    {
        var rng = new Lcg(seed);
        var query = new float[dimensions];
        for (var d = 0; d < dimensions; d++)
            query[d] = (float)((rng.NextDouble() * 2) - 1);

        query[index % dimensions] = 1f;
        return query;
    }

    /// <summary>
    /// Asserts the two result windows are the same window: same ids position by position, same 0-based
    /// ranks, and a score difference of exactly zero at every rank.
    /// </summary>
    private static void AssertSameWindow(
        IReadOnlyList<VectorSearchResult> shipped,
        IReadOnlyList<VectorSearchResult> quantized,
        string context)
    {
        Assert.AreEqual(shipped.Count, quantized.Count, $"{context}: the two windows must hold the same number of hits");

        var maxDelta = 0d;
        for (var rank = 0; rank < shipped.Count; rank++)
        {
            Assert.AreEqual(shipped[rank].Id, quantized[rank].Id, $"{context}: id at rank {rank}");
            Assert.AreEqual(rank, quantized[rank].Rank, $"{context}: rank field at position {rank}");
            maxDelta = Math.Max(maxDelta, Math.Abs(shipped[rank].Score - quantized[rank].Score));
        }

        Assert.IsTrue(
            maxDelta == 0d,
            $"{context}: the largest |score difference| is {maxDelta:R} but the two paths run the same "
            + "arithmetic sequence, so it must be exactly 0");
    }

    /// <summary>
    /// A1 — the shipped path's own semantics, pinned by hand-computed values, so "nothing changed" is a
    /// measurement rather than "the other tests still pass".
    /// </summary>
    [TestMethod]
    public void A1_Shipped_Search_Semantics_Are_Still_Pinned_By_Hand_Computed_Values()
    {
        var index = new InMemoryVectorIndex(2);
        index.Add(new VectorIndexEntry("b", "f.cs", 1, 2, "Outline", 2f, [3f, 4f]));
        index.Add(new VectorIndexEntry("a", "f.cs", 3, 4, "Outline", 2f, [1f, 0f]));
        index.Add(new VectorIndexEntry("c", "f.cs", 5, 6, "Outline", 2f, [-1f, 0f]));

        var results = index.Search([1f, 0f], 3);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, results.Select(r => r.Id).ToArray());
        Assert.AreEqual(1d, results[0].Score, "cos([1,0],[1,0]) = 1");
        Assert.AreEqual(0.6d, results[1].Score, "cos([1,0],[3,4]) = 3/5");
        Assert.AreEqual(-1d, results[2].Score, "cos([1,0],[-1,0]) = -1");
        Assert.AreEqual(0, results[0].Rank);
        Assert.AreEqual(1, results[1].Rank);
        Assert.AreEqual(2, results[2].Rank);
    }

    /// <summary>
    /// A2 — the in-place scan reproduces the shipped window bit for bit at three corpus sizes, including
    /// a <c>topK</c> larger than the corpus (where the bounded selection degenerates to "keep all").
    /// </summary>
    [TestMethod]
    public void A2_In_Place_Scan_Reproduces_The_Shipped_Ranking_Bit_For_Bit()
    {
        const int dimensions = 64;

        foreach (var rows in new[] { 37, 512, 4096 })
        {
            var (quantized, shipped) = BuildIndexes(rows, dimensions, seed: 20260924UL + (ulong)rows);

            for (var probe = 0; probe < 5; probe++)
            {
                var query = Query(dimensions, seed: 7UL + (ulong)(probe * 131), index: probe);
                AssertSameWindow(
                    shipped.Search(query, 20),
                    quantized.Search(query, 20),
                    $"rows={rows} query={probe} topK=20");
            }

            var wide = shipped.Search(Query(dimensions, seed: 991UL, index: 3), rows + 13);
            Assert.AreEqual(rows, wide.Count, $"rows={rows}: a topK above the corpus size must return every row");
            AssertSameWindow(
                wide,
                quantized.Search(Query(dimensions, seed: 991UL, index: 3), rows + 13),
                $"rows={rows} topK={rows + 13}");
        }
    }

    /// <summary>
    /// A3 — equal scores are broken by <see cref="QuantizedVectorEntry.Id"/> in <b>ordinal</b> order,
    /// exactly as the shipped sort does, regardless of insertion order or string culture. The ids are
    /// chosen so a culture-aware or insertion-order tie-break would produce a different sequence.
    /// </summary>
    [TestMethod]
    public void A3_Ties_Are_Broken_By_Id_Ordinal_Not_By_Insertion_Order()
    {
        var identical = VectorQuantizer.Quantize([0.5f, -0.25f, 1f]);

        var quantized = new QuantizedInMemoryVectorIndex(3);
        var shipped = new InMemoryVectorIndex(3);
        foreach (var id in new[] { "b-chunk", "a-chunk", "B-chunk", "A-chunk" })
        {
            var row = new QuantizedVectorEntry(id, "Source/Demo/Alpha.cs", 12, 20, "Outline", 2f, identical);
            quantized.Add(row);
            shipped.Add(row.ToVectorEntry());
        }

        // Ordinal order: 'A' (65) < 'B' (66) < 'a' (97) < 'b' (98). A culture-aware comparison would put
        // "a-chunk" before "B-chunk", and insertion order would start with "b-chunk".
        var expected = new[] { "A-chunk", "B-chunk", "a-chunk", "b-chunk" };

        // The query equals the direction all four rows share, so their scores are equal by construction
        // and only the tie-break decides the order.
        var query = identical.Dequantize();

        var quantizedTop = quantized.Search(query, 4);
        var shippedTop = shipped.Search(query, 4);

        CollectionAssert.AreEqual(expected, quantizedTop.Select(r => r.Id).ToArray());
        CollectionAssert.AreEqual(expected, shippedTop.Select(r => r.Id).ToArray());
        AssertSameWindow(shippedTop, quantizedTop, "four equally-scored rows");

        var quantizedTop2 = quantized.Search(query, 2);
        CollectionAssert.AreEqual(expected[..2], quantizedTop2.Select(r => r.Id).ToArray());
        AssertSameWindow(shipped.Search(query, 2), quantizedTop2, "two equally-scored rows");

        Assert.IsTrue(
            quantizedTop.All(hit => hit.Score == quantizedTop[0].Score),
            "all four rows must carry exactly the same score, otherwise the order would not be a tie-break");
        Assert.AreEqual(1d, quantizedTop[0].Score, 1e-12, "a row against its own direction scores 1");
    }

    /// <summary>
    /// A4 (task book R5) — a query allocates for k winners, not for N rows: the in-place scan must stay
    /// under a quarter of the shipped path's allocation at N=4096, k=20, and its per-query allocation
    /// must not grow when the corpus doubles.
    /// </summary>
    [TestMethod]
    public void A4_Query_Allocation_Is_Bounded_By_K_Not_By_The_Corpus()
    {
        const int dimensions = 256;
        const int topK = 20;
        const int repetitions = 50;

        var small = BuildIndexes(4096, dimensions, seed: 4242UL);
        var large = BuildIndexes(8192, dimensions, seed: 4242UL);
        var query = Query(dimensions, seed: 5150UL, index: 7);

        var shippedPerQuery = MeasurePerQuery(() => small.Shipped.Search(query, topK), repetitions);
        var quantizedPerQuery = MeasurePerQuery(() => small.Quantized.Search(query, topK), repetitions);
        var quantizedDoubled = MeasurePerQuery(() => large.Quantized.Search(query, topK), repetitions);

        Console.WriteLine(
            $"A4 allocation per query (N=4096, k=20, {repetitions} repetitions): "
            + $"shipped={shippedPerQuery:0} B, in-place={quantizedPerQuery:0} B, in-place at N=8192={quantizedDoubled:0} B");

        Assert.IsTrue(
            quantizedPerQuery * 4d <= shippedPerQuery,
            $"the in-place scan must allocate at most a quarter of the shipped path: "
            + $"shipped={shippedPerQuery:0} B/query, in-place={quantizedPerQuery:0} B/query "
            + $"(ratio 1:{shippedPerQuery / Math.Max(quantizedPerQuery, 1d):0.0})");

        Assert.IsTrue(
            quantizedDoubled <= quantizedPerQuery * 2d,
            "doubling the corpus must not scale the in-place scan's allocation: "
            + $"N=4096 -> {quantizedPerQuery:0} B/query, N=8192 -> {quantizedDoubled:0} B/query");
    }

    /// <summary>Warms the path up, then returns the allocated bytes per call.</summary>
    private static double MeasurePerQuery(Func<IReadOnlyList<VectorSearchResult>> call, int repetitions)
    {
        for (var warmup = 0; warmup < 3; warmup++)
            GC.KeepAlive(call());

        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < repetitions; i++)
            GC.KeepAlive(call());

        var after = GC.GetTotalAllocatedBytes(precise: true);
        return (after - before) / (double)repetitions;
    }

    /// <summary>
    /// A5 — the refusals are the same on both paths: empty corpus, wrong query length, zero query, bad
    /// topK, duplicate id, all-zero row and a row of the wrong dimension.
    /// </summary>
    [TestMethod]
    public void A5_Fail_Closed_Rules_Are_Identical_On_Both_Paths()
    {
        var emptyQuantized = new QuantizedInMemoryVectorIndex(3);
        var emptyShipped = new InMemoryVectorIndex(3);

        // An empty corpus is a measurement (no hits), not an error — on both paths.
        Assert.AreEqual(0, emptyQuantized.Search([1f, 0f, 0f], 5).Count);
        Assert.AreEqual(0, emptyShipped.Search([1f, 0f, 0f], 5).Count);

        // The query is validated before the corpus is looked at, so an empty index still refuses these.
        Assert.ThrowsExactly<ArgumentException>(() => emptyQuantized.Search([1f, 0f], 5));
        Assert.ThrowsExactly<ArgumentException>(() => emptyShipped.Search([1f, 0f], 5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => emptyQuantized.Search([1f, 0f, 0f], 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => emptyShipped.Search([1f, 0f, 0f], 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => emptyQuantized.Search([0f, 0f, 0f], 5));
        Assert.ThrowsExactly<InvalidOperationException>(() => emptyShipped.Search([0f, 0f, 0f], 5));

        var quantized = new QuantizedInMemoryVectorIndex(3);
        var shipped = new InMemoryVectorIndex(3);
        var row = Row("dup", [1f, -2f, 0.5f]);
        quantized.Add(row);
        shipped.Add(row.ToVectorEntry());

        // Duplicate id.
        Assert.ThrowsExactly<ArgumentException>(() => quantized.Add(row));
        Assert.ThrowsExactly<ArgumentException>(() => shipped.Add(row.ToVectorEntry()));

        // All-zero row: no direction, so no score may be invented for it.
        var zeroRow = new QuantizedVectorEntry(
            "zero", "Source/Demo/Alpha.cs", 1, 2, "Outline", 1f, new QuantizedVector(new sbyte[3], 1f));
        Assert.ThrowsExactly<ArgumentException>(() => quantized.Add(zeroRow));
        Assert.ThrowsExactly<ArgumentException>(
            () => shipped.Add(new VectorIndexEntry("zero", "Source/Demo/Alpha.cs", 1, 2, "Outline", 1f, [0f, 0f, 0f])));

        // A row of the wrong dimension is refused, never truncated.
        var shortRow = new QuantizedVectorEntry(
            "short", "Source/Demo/Alpha.cs", 1, 2, "Outline", 1f, new QuantizedVector([1, 2], 0.5f));
        Assert.ThrowsExactly<ArgumentException>(() => quantized.Add(shortRow));
        Assert.ThrowsExactly<ArgumentException>(
            () => shipped.Add(new VectorIndexEntry("short", "Source/Demo/Alpha.cs", 1, 2, "Outline", 1f, [1f, 2f])));

        // A rejected row must not have been recorded.
        Assert.AreEqual(1, quantized.Count);
        Assert.AreEqual(1, quantized.Entries.Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuantizedInMemoryVectorIndex(0));
    }

    /// <summary>The scan keeps the provenance a report needs, and its route, without rebuilding the codes.</summary>
    [TestMethod]
    public void Rows_Keep_Provenance_And_Route_Through_The_Scan()
    {
        var index = new QuantizedInMemoryVectorIndex(2, EmbeddingRoute.Parse("stub-provider/stub-model"));
        index.Add(Row("id-1", [1f, 0f]));

        var hit = index.Search([1f, 0f], 1)[0];

        Assert.AreEqual("Source/Demo/Alpha.cs", hit.SourceFile);
        Assert.AreEqual(12, hit.StartLine);
        Assert.AreEqual(20, hit.EndLine);
        Assert.AreEqual("Outline", hit.Kind);
        Assert.AreEqual(new EmbeddingRoute("stub-provider", "stub-model"), index.Route);
        Assert.AreEqual(1, index.Count);

        // The stored row is still a quantised row: codes plus the one scale, not a float32 copy.
        var stored = index.Entries[0];
        Assert.AreEqual(2, stored.Dimensions);
        Assert.AreEqual(2 + sizeof(float), stored.StorageBytes);
        Assert.AreEqual(
            (sbyte)VectorQuantizer.MaxCode,
            stored.Vector.Codes.Span[0],
            "the stored row is still int8 codes: the largest magnitude of [1, 0] maps onto the largest code");
        Assert.AreEqual(VectorQuantizer.Quantize([1f, 0f]).Scale, stored.Vector.Scale);
    }
}
