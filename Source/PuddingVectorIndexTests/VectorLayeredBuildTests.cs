using PuddingVectorIndex;

namespace PuddingVectorIndexTests;

/// <summary>
/// Contract tests for the two U4-3 mechanisms as seen from the builder (task book §5, assertions
/// A1 and A5–A9): layered selection is a <b>build-time</b> filter, quantisation changes storage without
/// changing the similarity rule, and both are off unless a caller turns them on.
/// </summary>
[TestClass]
public sealed class VectorLayeredBuildTests
{
    /// <summary>12 documents: 5 Outline (P0), 4 DocComment (P1), 3 CodeText (P2), tiers interleaved.</summary>
    private static List<VectorDocument> MixedDocuments()
    {
        string[] tiers = ["Outline", "DocComment", "CodeText", "Outline", "DocComment", "CodeText", "Outline", "DocComment", "CodeText", "Outline", "DocComment", "Outline"];

        return Enumerable.Range(0, tiers.Length)
            .Select(i => new VectorDocument(
                $"d{i:00}",
                $"Source/Demo/File{i:00}.cs",
                i + 1,
                i + 3,
                tiers[i],
                boost: 1f,
                text: $"public class Thing{i:00} {{ }}"))
            .ToList();
    }

    [TestMethod]
    public async Task A1_Default_Options_Keep_The_Unfiltered_Unquantised_Build()
    {
        var documents = MixedDocuments();

        // The stub is seeded from the document text, so "the stored vector equals the provider's own output"
        // is a decidable claim rather than a comparison of two identical constants.
        static StubEmbeddingProvider Provider() => new(5, text => StubEmbeddingProvider.DefaultVector(5, text));

        var result = await VectorIndexBuilder.BuildAsync(Provider(), documents);

        Assert.AreEqual(documents.Count, result.DocumentCount);
        Assert.AreEqual(0, result.SkippedByTierCount);
        Assert.AreEqual(documents.Count, result.IndexedCount);
        Assert.IsNull(result.EmptyReason, "a non-empty unfiltered build is never 'empty for a reason'");
        Assert.IsEmpty(result.QuantizedEntries, "nothing may be quantised unless the caller asked for it");

        // The stored vectors must be the provider's raw output — no round trip, no rescaling.
        for (var i = 0; i < documents.Count; i++)
        {
            CollectionAssert.AreEqual(
                StubEmbeddingProvider.DefaultVector(5, documents[i].Text),
                result.Index.Entries[i].Vector.Span.ToArray(),
                $"document {i} must keep the untouched embedding when quantisation is off");
        }

        // Two builds of the same input are bit-for-bit identical (the same claim as the reporter's
        // "f32-all reproduces the U4-1b baseline", one level down).
        var again = await VectorIndexBuilder.BuildAsync(
            Provider(), documents, new VectorIndexBuildOptions(batchSize: 32));

        CollectionAssert.AreEqual(
            result.Index.Entries.Select(entry => entry.Id).ToArray(),
            again.Index.Entries.Select(entry => entry.Id).ToArray());

        for (var i = 0; i < result.Index.Count; i++)
        {
            CollectionAssert.AreEqual(
                result.Index.Entries[i].Vector.Span.ToArray(),
                again.Index.Entries[i].Vector.Span.ToArray(),
                $"entry {i} must be identical across two builds");
        }
    }

    [TestMethod]
    public async Task A5_Quantised_Search_Is_Repeatable_And_Breaks_Ties_By_Id_Ordinal()
    {
        // Four documents that quantise to exactly the same codes, inserted in reverse id order. Their
        // scores are therefore exactly equal and the ordinal id rule is the only thing left to decide the
        // window — which is also the rule the store's row order depends on.
        string[] ids = ["zeta", "yankee", "xray", "alpha"];
        var documents = ids
            .Select(id => new VectorDocument(id, $"Source/Demo/{id}.cs", 1, 2, "Outline", 1f, $"public void {id}() {{ }}"))
            .ToList();

        float[] fixedVector = [0.25f, -0.5f, 0.125f, 1f];
        var provider = new StubEmbeddingProvider(4, _ => fixedVector);

        var result = await VectorIndexBuilder.BuildAsync(
            provider, documents, new VectorIndexBuildOptions(32, quantize: true));

        Assert.AreEqual(4, result.QuantizedEntries.Count);
        Assert.AreEqual(4, result.Index.Count);

        var first = result.Index.Search(fixedVector, 4);
        var second = result.Index.Search(fixedVector, 4);

        for (var i = 1; i < first.Count; i++)
        {
            Assert.AreEqual(
                first[0].Score,
                first[i].Score,
                "the tie must be real; otherwise the ordering assertion below proves nothing");
        }

        CollectionAssert.AreEqual(
            ids.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            first.Select(hit => hit.Id).ToArray(),
            "equal scores must be resolved by the id in ordinal order");

        CollectionAssert.AreEqual(
            first.Select(hit => hit.Id).ToArray(),
            second.Select(hit => hit.Id).ToArray(),
            "two searches over the same store must return the same window");

        // The index holds the round-tripped values, i.e. what a store loaded from disk would search.
        var roundTripped = VectorQuantizer.Quantize(fixedVector).Dequantize();
        CollectionAssert.AreEqual(
            roundTripped,
            result.Index.Entries[0].Vector.Span.ToArray(),
            "the search side must see the dequantised values, not the original embedding");

        Assert.AreNotEqual(
            fixedVector[1],
            result.Index.Entries[0].Vector.Span[1],
            "-0.5 is not representable with this scale; if it were, this test could not tell storage "
            + "format from no storage format at all");
    }

    [TestMethod]
    public async Task A6_The_Tier_Filter_Keeps_Exactly_The_Selected_Blocks()
    {
        var documents = MixedDocuments();
        var expectedOutlines = documents.Where(document => document.Kind == "Outline").ToArray();

        var result = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(4),
            documents,
            new VectorIndexBuildOptions(32, VectorTierFilter.OutlineOnly));

        Assert.AreEqual(documents.Count, result.DocumentCount, "the input count is reported unchanged");
        Assert.AreEqual(documents.Count - expectedOutlines.Length, result.SkippedByTierCount);
        Assert.AreEqual(expectedOutlines.Length, result.IndexedCount);

        // Checked block by block, not sampled.
        CollectionAssert.AreEqual(
            expectedOutlines.Select(document => document.Id).ToArray(),
            result.Index.Entries.Select(entry => entry.Id).ToArray());

        Assert.IsTrue(
            result.Index.Entries.All(entry => entry.Kind == "Outline"),
            "no other tier may reach the index");
    }

    [TestMethod]
    public async Task A7_Filtering_Only_Removes_Blocks_It_Never_Changes_The_Survivors()
    {
        var documents = MixedDocuments();

        // Batch size 2 makes "did an excluded block cost a round trip?" decidable: 5 survivors take 3
        // calls, the whole corpus takes 6. That difference is what distinguishes a build-time filter from
        // a post-hoc trim, and it is the economy the design is paying for.
        var filtered = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(4),
            documents,
            new VectorIndexBuildOptions(2, VectorTierFilter.OutlineOnly));

        var unfiltered = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(4),
            documents,
            new VectorIndexBuildOptions(2));

        Assert.AreEqual(3, filtered.EmbeddingCalls, "an excluded block must not be embedded at all");
        Assert.AreEqual(6, unfiltered.EmbeddingCalls);

        var expected = unfiltered.Index.Entries
            .Where(entry => entry.Kind == "Outline")
            .ToArray();

        Assert.AreEqual(expected.Length, filtered.Index.Count);

        // Same blocks, same order, same content — the filtered build differs from the unfiltered one only
        // by the absence of the other tiers.
        CollectionAssert.AreEqual(
            expected.Select(entry => entry.Id).ToArray(),
            filtered.Index.Entries.Select(entry => entry.Id).ToArray());

        for (var i = 0; i < expected.Length; i++)
        {
            var survivor = filtered.Index.Entries[i];
            Assert.AreEqual(expected[i].SourceFile, survivor.SourceFile);
            Assert.AreEqual(expected[i].StartLine, survivor.StartLine);
            Assert.AreEqual(expected[i].EndLine, survivor.EndLine);
            Assert.AreEqual(expected[i].Kind, survivor.Kind);
            Assert.AreEqual(expected[i].Boost, survivor.Boost);
            CollectionAssert.AreEqual(
                expected[i].Vector.Span.ToArray(),
                survivor.Vector.Span.ToArray(),
                $"surviving entry {i} must carry the identical vector");
        }
    }

    [TestMethod]
    public async Task A8_Filtered_Empty_Is_Reported_As_Filtered_Empty_Not_As_Nothing_To_Index()
    {
        var nonOutlines = MixedDocuments().Where(document => document.Kind != "Outline").ToList();

        var filtered = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(4),
            nonOutlines,
            new VectorIndexBuildOptions(32, VectorTierFilter.OutlineOnly));

        Assert.AreEqual(0, filtered.Index.Count);
        Assert.AreEqual(nonOutlines.Count, filtered.SkippedByTierCount);
        Assert.AreEqual(0, filtered.EmbeddingCalls, "nothing is embedded when nothing passed the filter");
        Assert.IsNotNull(filtered.EmptyReason, "a filtered-empty build must say so");
        StringAssert.Contains(filtered.EmptyReason, "tier filter");

        var emptyInput = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(4),
            [],
            new VectorIndexBuildOptions(32, VectorTierFilter.OutlineOnly));

        Assert.IsNotNull(emptyInput.EmptyReason, "an empty input must say so too");
        StringAssert.Contains(emptyInput.EmptyReason, "no documents");

        Assert.AreNotEqual(
            filtered.EmptyReason,
            emptyInput.EmptyReason,
            "the two causes of an empty index must not share one sentence");

        // Control: the same input without a filter is not empty and carries no reason.
        var noFilter = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(4),
            nonOutlines,
            new VectorIndexBuildOptions(32));

        Assert.AreEqual(nonOutlines.Count, noFilter.Index.Count);
        Assert.IsNull(noFilter.EmptyReason);
    }

    [TestMethod]
    public async Task A9_Int8_Storage_Is_Smaller_Than_Float32_And_P0_Is_Smaller_Than_Every_Tier()
    {
        Assert.AreEqual(4096L, VectorQuantizer.Float32StorageBytes(1024));
        Assert.AreEqual(1028L, VectorQuantizer.Int8StorageBytes(1024));
        Assert.IsTrue(VectorQuantizer.Int8StorageBytes(1024) < VectorQuantizer.Float32StorageBytes(1024));
        Assert.AreEqual(3.9844d, (double)VectorQuantizer.Float32StorageBytes(1024) / VectorQuantizer.Int8StorageBytes(1024), 1e-3);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VectorQuantizer.Int8StorageBytes(0));

        var documents = MixedDocuments();

        var quantised = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(1024),
            documents,
            new VectorIndexBuildOptions(32, quantize: true));

        Assert.AreEqual(documents.Count, quantised.QuantizedEntries.Count);
        Assert.AreEqual(1028, quantised.QuantizedEntries[0].StorageBytes, "1024 codes plus one scale");
        Assert.AreEqual(
            (long)documents.Count * 1028,
            quantised.QuantizedEntries.Sum(entry => (long)entry.StorageBytes));
        Assert.AreEqual(0, quantised.SkippedByTierCount);

        var layered = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(1024),
            documents,
            new VectorIndexBuildOptions(32, VectorTierFilter.OutlineOnly, quantize: true));

        Assert.IsTrue(
            layered.IndexedCount < quantised.IndexedCount,
            "building vectors for P0 only must index fewer blocks than building every tier");
        Assert.AreEqual(5, layered.IndexedCount);
        Assert.AreEqual(5, layered.QuantizedEntries.Count, "the stored rows must be the filtered ones");

        // The quantised entry is also the only bridge to the search-side entry.
        var roundTripped = layered.QuantizedEntries[0].ToVectorEntry();
        Assert.AreEqual(layered.QuantizedEntries[0].Id, roundTripped.Id);
        CollectionAssert.AreEqual(
            layered.QuantizedEntries[0].Vector.Dequantize(),
            roundTripped.Vector.Span.ToArray());
    }

    [TestMethod]
    public async Task Tier_Filter_Rejects_Blank_Names_And_The_Builder_Rejects_A_Blank_Tier_Label()
    {
        Assert.ThrowsExactly<ArgumentException>(() => VectorTierFilter.Of());
        Assert.ThrowsExactly<ArgumentException>(() => VectorTierFilter.Of("Outline", "  "));
        Assert.ThrowsExactly<ArgumentNullException>(() => VectorTierFilter.Of(null!));

        var filter = VectorTierFilter.Of("DocComment", "Outline");
        Assert.AreEqual("DocComment, Outline", filter.Description, "the description is ordinal-sorted so reports are stable");
        Assert.IsTrue(filter.Includes("Outline"));
        Assert.IsFalse(filter.Includes("outline"), "comparison is ordinal: a mislabeled tier is loud, not fuzzy");
        Assert.IsFalse(filter.Includes(null!));

        // A mislabeled tier yields the filtered-empty report rather than a silent empty index.
        var result = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(3),
            MixedDocuments(),
            new VectorIndexBuildOptions(32, VectorTierFilter.Of("outline")));

        Assert.AreEqual(0, result.Index.Count);
        Assert.IsNotNull(result.EmptyReason);
        StringAssert.Contains(result.EmptyReason, "outline");
    }
}
