using PuddingVectorIndex;

namespace PuddingVectorIndexTests;

/// <summary>
/// Index behaviour: dimension/duplicate/zero-vector rejection, deterministic ranking, and the
/// "different dimension fails, never truncates" rule that the whole comparison depends on.
/// </summary>
[TestClass]
public sealed class InMemoryVectorIndexTests
{
    private static VectorIndexEntry Entry(string id, float[] vector, string file = "src/a.cs", int start = 1, int end = 2) =>
        new(id, file, start, end, "Outline", 2f, vector);

    [TestMethod]
    public void Search_Ranks_By_Cosine_Descending()
    {
        var index = new InMemoryVectorIndex(3);
        index.Add(Entry("far", [0f, 0f, 1f]));
        index.Add(Entry("near", [0.9f, 0.1f, 0f]));
        index.Add(Entry("exact", [1f, 0f, 0f]));

        var results = index.Search([1f, 0f, 0f], 3);

        CollectionAssert.AreEqual(new[] { "exact", "near", "far" }, results.Select(r => r.Id).ToArray());
        Assert.AreEqual(0, results[0].Rank);
        Assert.AreEqual(1, results[1].Rank);
        Assert.AreEqual(2, results[2].Rank);
        Assert.AreEqual(1d, results[0].Score, 1e-9);
        Assert.IsTrue(results[0].Score > results[1].Score && results[1].Score > results[2].Score,
            "scores must be strictly decreasing for these vectors");
    }

    [TestMethod]
    public void Search_Breaks_Ties_By_Id_So_Two_Runs_Agree()
    {
        var index = new InMemoryVectorIndex(2);
        index.Add(Entry("charlie", [1f, 0f]));
        index.Add(Entry("alpha", [1f, 0f]));
        index.Add(Entry("bravo", [1f, 0f]));

        var first = index.Search([1f, 0f], 3).Select(r => r.Id).ToArray();
        var second = index.Search([1f, 0f], 3).Select(r => r.Id).ToArray();

        CollectionAssert.AreEqual(new[] { "alpha", "bravo", "charlie" }, first);
        CollectionAssert.AreEqual(first, second);
    }

    [TestMethod]
    public void Search_Respects_TopK_And_Returns_Whole_Index_When_Asked_For_More()
    {
        var index = new InMemoryVectorIndex(2);
        index.Add(Entry("a", [1f, 0f]));
        index.Add(Entry("b", [0.5f, 0.5f]));

        Assert.AreEqual(1, index.Search([1f, 0f], 1).Count);
        Assert.AreEqual(2, index.Search([1f, 0f], 10).Count);
    }

    [TestMethod]
    public void Search_On_Empty_Index_Returns_Empty_List()
    {
        var index = new InMemoryVectorIndex(4);

        Assert.AreEqual(0, index.Count);
        Assert.AreEqual(0, index.Search([1f, 0f, 0f, 0f], 5).Count);
    }

    [TestMethod]
    public void Add_Rejects_Dimension_Mismatch_Instead_Of_Truncating()
    {
        var index = new InMemoryVectorIndex(3);

        var exception = Assert.ThrowsExactly<ArgumentException>(() => index.Add(Entry("short", [1f, 0f])));

        Assert.Contains("dimension mismatch", exception.Message);
        Assert.AreEqual(0, index.Count, "a rejected entry must not be recorded");
    }

    [TestMethod]
    public void Add_Rejects_Duplicate_Id_And_Zero_Vector()
    {
        var index = new InMemoryVectorIndex(2);
        index.Add(Entry("dup", [1f, 0f]));

        Assert.ThrowsExactly<ArgumentException>(() => index.Add(Entry("dup", [0f, 1f])));
        Assert.ThrowsExactly<ArgumentException>(() => index.Add(Entry("zero", [0f, 0f])));
        Assert.AreEqual(1, index.Count);
    }

    [TestMethod]
    public void Search_Rejects_Bad_Query_Dimension_Norm_And_TopK()
    {
        var index = new InMemoryVectorIndex(3);
        index.Add(Entry("a", [1f, 0f, 0f]));

        Assert.ThrowsExactly<ArgumentException>(() => index.Search([1f, 0f], 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => index.Search([0f, 0f, 0f], 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => index.Search([1f, 0f, 0f], 0));
    }

    [TestMethod]
    public void Index_Rejects_A_Non_Positive_Dimension()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InMemoryVectorIndex(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InMemoryVectorIndex(-1));
    }

    [TestMethod]
    public void Entry_Copies_The_Vector_So_Later_Mutation_Cannot_Repoint_It()
    {
        var vector = new float[] { 1f, 0f };
        var index = new InMemoryVectorIndex(2);
        index.Add(Entry("a", vector));

        vector[0] = 999f;

        Assert.AreEqual(1f, index.Entries[0].Vector.Span[0]);
        Assert.AreEqual(1d, index.Search([1f, 0f], 1)[0].Score, 1e-9);
    }

    [TestMethod]
    public void Entry_Records_Provenance_And_Tier()
    {
        var index = new InMemoryVectorIndex(2, EmbeddingRoute.Parse("stub-provider/stub-model"));
        index.Add(new VectorIndexEntry("id-1", "Source/Demo/Alpha.cs", 12, 20, "DocComment", 1.5f, [1f, 0f]));

        var hit = index.Search([1f, 0f], 1)[0];

        Assert.AreEqual("Source/Demo/Alpha.cs", hit.SourceFile);
        Assert.AreEqual(12, hit.StartLine);
        Assert.AreEqual(20, hit.EndLine);
        Assert.AreEqual("DocComment", hit.Kind);
        Assert.AreEqual(new EmbeddingRoute("stub-provider", "stub-model"), index.Route);
    }

    [TestMethod]
    public void Route_Parses_Provider_And_Model_Keeping_Slashes_In_The_Model_Id()
    {
        var route = EmbeddingRoute.Parse("lmstudio-local/text-embedding-qwen3-embedding-0.6b");
        Assert.AreEqual("lmstudio-local", route.ProviderId);
        Assert.AreEqual("text-embedding-qwen3-embedding-0.6b", route.ModelId);
        Assert.AreEqual("lmstudio-local/text-embedding-qwen3-embedding-0.6b", route.ToString());

        var nested = EmbeddingRoute.Parse("local/qwen/qwen3.6-35b-a3b");
        Assert.AreEqual("local", nested.ProviderId);
        Assert.AreEqual("qwen/qwen3.6-35b-a3b", nested.ModelId);

        Assert.ThrowsExactly<ArgumentException>(() => EmbeddingRoute.Parse("no-slash"));
        Assert.ThrowsExactly<ArgumentException>(() => EmbeddingRoute.Parse("provider/"));
        Assert.ThrowsExactly<ArgumentException>(() => EmbeddingRoute.Parse("/model"));
        Assert.ThrowsExactly<ArgumentException>(() => EmbeddingRoute.Parse("   "));
    }
}
