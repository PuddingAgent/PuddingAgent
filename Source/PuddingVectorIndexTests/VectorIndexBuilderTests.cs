using PuddingVectorIndex;

namespace PuddingVectorIndexTests;

/// <summary>
/// The builder's contract with the port: batch and single embedding must be indistinguishable in the
/// resulting index, the input/output order must be preserved, and every way a provider can misbehave
/// must fail loudly rather than produce a quietly wrong index.
/// </summary>
[TestClass]
public sealed class VectorIndexBuilderTests
{
    private static VectorDocument Document(string id, string text) =>
        new(id, "Source/Demo/Alpha.cs", 1, 3, "Outline", 2f, text);

    private static List<VectorDocument> Documents(int count) =>
        Enumerable.Range(0, count).Select(i => Document($"doc-{i:00}", $"public class Thing{i} {{ }}")).ToList();

    [TestMethod]
    public async Task Empty_Input_Produces_An_Empty_Index_And_No_Calls()
    {
        var provider = new StubEmbeddingProvider(8);

        var result = await VectorIndexBuilder.BuildAsync(provider, []);

        Assert.AreEqual(0, result.DocumentCount);
        Assert.AreEqual(0, result.Index.Count);
        Assert.AreEqual(0, result.EmbeddingCalls);
        Assert.AreEqual(0L, result.EmbeddingMs);
        Assert.AreEqual(8, result.Dimensions);
        Assert.AreEqual(0, provider.BatchCalls);
        Assert.AreEqual(0, result.Index.Search([1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f], 5).Count);
    }

    [TestMethod]
    public async Task Batch_And_Single_Embedding_Produce_The_Same_Index()
    {
        var documents = Documents(10);
        var single = new StubEmbeddingProvider(5);
        var batched = new StubEmbeddingProvider(5);

        var oneAtATime = await VectorIndexBuilder.BuildAsync(
            single, documents, new VectorIndexBuildOptions(batchSize: 1));
        var fourAtATime = await VectorIndexBuilder.BuildAsync(
            batched, documents, new VectorIndexBuildOptions(batchSize: 4));

        Assert.AreEqual(10, oneAtATime.EmbeddingCalls, "batch size 1 must issue one call per document");
        Assert.AreEqual(3, fourAtATime.EmbeddingCalls, "10 documents in batches of 4 must take 3 round trips");
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, single.BatchSizes.ToArray());
        CollectionAssert.AreEqual(new[] { 4, 4, 2 }, batched.BatchSizes.ToArray());

        Assert.AreEqual(oneAtATime.Index.Count, fourAtATime.Index.Count);
        CollectionAssert.AreEqual(
            oneAtATime.Index.Entries.Select(e => e.Id).ToArray(),
            fourAtATime.Index.Entries.Select(e => e.Id).ToArray());

        for (var i = 0; i < oneAtATime.Index.Count; i++)
            CollectionAssert.AreEqual(
                oneAtATime.Index.Entries[i].Vector.Span.ToArray(),
                fourAtATime.Index.Entries[i].Vector.Span.ToArray(),
                $"document {i} must receive the same vector regardless of batching");

        var query = StubEmbeddingProvider.DefaultVector(5, documents[3].Text);
        var fromSingle = oneAtATime.Index.Search(query, 10);
        var fromBatched = fourAtATime.Index.Search(query, 10);

        Assert.AreEqual(fromSingle.Count, fromBatched.Count);
        for (var i = 0; i < fromSingle.Count; i++)
        {
            Assert.AreEqual(fromSingle[i].Id, fromBatched[i].Id, $"rank {i} must match");
            Assert.AreEqual(fromSingle[i].Score, fromBatched[i].Score, 1e-12);
        }
    }

    [TestMethod]
    public async Task Vector_Belongs_To_Its_Own_Document_Regardless_Of_Batching()
    {
        // Orthogonal basis vectors make a batch-alignment bug decidable: document i must come back
        // with the basis vector for slot i, no matter how the batches were cut.
        var documents = Enumerable.Range(0, 7)
            .Select(i => Document($"doc-{i}", $"text-{i}"))
            .ToList();

        var provider = new StubEmbeddingProvider(
            7,
            text => StubEmbeddingProvider.BasisVector(7, int.Parse(text["text-".Length..])));

        var result = await VectorIndexBuilder.BuildAsync(
            provider, documents, new VectorIndexBuildOptions(batchSize: 3));

        Assert.AreEqual(7, result.Index.Count);
        for (var i = 0; i < 7; i++)
        {
            var entry = result.Index.Entries[i];
            Assert.AreEqual($"doc-{i}", entry.Id);
            Assert.AreEqual(1f, entry.Vector.Span[i], $"document {i} must own slot {i}");
            var values = entry.Vector.Span.ToArray();
            Assert.AreEqual(1f, values.Sum(), $"document {i} must be the unit basis vector");
        }

        var hit = result.Index.Search(StubEmbeddingProvider.BasisVector(7, 5), 1);
        Assert.AreEqual("doc-5", hit[0].Id);
        Assert.AreEqual(1d, hit[0].Score, 1e-9);
    }

    [TestMethod]
    public async Task Unavailable_Provider_Fails_Instead_Of_Building_An_Empty_Index()
    {
        var provider = new StubEmbeddingProvider(4, isAvailable: false);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => VectorIndexBuilder.BuildAsync(provider, Documents(3)));

        StringAssert.Contains(exception.Message, "not available");
        Assert.AreEqual(0, provider.BatchCalls);
    }

    [TestMethod]
    public async Task Undeclared_Dimension_Fails_Closed()
    {
        var provider = new StubEmbeddingProvider(0);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => VectorIndexBuilder.BuildAsync(provider, Documents(3)));

        StringAssert.Contains(exception.Message, "dimensions");
    }

    [TestMethod]
    public async Task Provider_Returning_Wrong_Vector_Length_Fails_Closed()
    {
        var provider = new StubEmbeddingProvider(4) { ReturnDimensionOverride = 2 };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => VectorIndexBuilder.BuildAsync(provider, Documents(3)));

        StringAssert.Contains(exception.Message, "declared 4 dimensions but returned 2");
    }

    [TestMethod]
    public async Task Provider_Returning_A_Short_Batch_Fails_Closed()
    {
        var provider = new StubEmbeddingProvider(4) { TruncateBatchTo = 2 };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => VectorIndexBuilder.BuildAsync(provider, Documents(6), new VectorIndexBuildOptions(batchSize: 4)));

        StringAssert.Contains(exception.Message, "for a batch of");
    }

    [TestMethod]
    public async Task Provider_Returning_A_Null_Vector_Fails_Closed()
    {
        var provider = new StubEmbeddingProvider(4) { NullVectorAtIndex = 1 };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => VectorIndexBuilder.BuildAsync(provider, Documents(4), new VectorIndexBuildOptions(batchSize: 4)));
    }

    [TestMethod]
    public void Non_Positive_Batch_Size_Is_Rejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VectorIndexBuildOptions(batchSize: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VectorIndexBuildOptions(batchSize: -5));
    }

    [TestMethod]
    public async Task Described_Capability_Fields_Reach_The_Index()
    {
        var provider = new StubEmbeddingProvider(
            16,
            isAvailable: true,
            maxContextTokens: 8192,
            isLocal: true,
            providerId: "lmstudio-local",
            modelId: "text-embedding-qwen3-embedding-0.6b");

        var described = provider.Describe();
        Assert.AreEqual(16, described.Dimensions);
        Assert.AreEqual(8192, described.MaxContextTokens);
        Assert.IsTrue(described.IsLocal);
        Assert.IsTrue(described.IsAvailable);
        Assert.AreEqual("lmstudio-local/text-embedding-qwen3-embedding-0.6b", described.Route.ToString());

        var result = await VectorIndexBuilder.BuildAsync(provider, Documents(2));

        Assert.AreEqual(described.Dimensions, result.Dimensions);
        Assert.AreEqual(described.Dimensions, result.Index.Dimensions);
        Assert.AreEqual(described.Route, result.Index.Route);
    }

    [TestMethod]
    public async Task Documents_Keep_Their_Provenance_Through_The_Builder()
    {
        var documents = new List<VectorDocument>
        {
            new("a", "Source/Demo/Alpha.cs", 4, 9, "DocComment", 1.5f, "alpha text"),
            new("b", "Source/Demo/Beta.cs", 10, 12, "CodeText", 1f, "beta text"),
        };

        var result = await VectorIndexBuilder.BuildAsync(
            new StubEmbeddingProvider(3), documents, new VectorIndexBuildOptions(batchSize: 8));

        Assert.AreEqual("Source/Demo/Alpha.cs", result.Index.Entries[0].SourceFile);
        Assert.AreEqual(4, result.Index.Entries[0].StartLine);
        Assert.AreEqual(9, result.Index.Entries[0].EndLine);
        Assert.AreEqual("DocComment", result.Index.Entries[0].Kind);
        Assert.AreEqual(1.5f, result.Index.Entries[0].Boost);
        Assert.AreEqual("Source/Demo/Beta.cs", result.Index.Entries[1].SourceFile);
    }

    [TestMethod]
    public async Task Document_Validation_Rejects_Blank_Text_And_Impossible_Ranges()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Document("id", "   "));
        Assert.ThrowsExactly<ArgumentException>(() => new VectorDocument("", "f.cs", 1, 1, "Outline", 1f, "t"));
        Assert.ThrowsExactly<ArgumentException>(() => new VectorDocument("id", " ", 1, 1, "Outline", 1f, "t"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VectorDocument("id", "f.cs", 0, 1, "Outline", 1f, "t"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VectorDocument("id", "f.cs", 5, 4, "Outline", 1f, "t"));

        await Task.CompletedTask;
    }
}
