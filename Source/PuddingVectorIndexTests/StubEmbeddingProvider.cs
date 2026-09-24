using PuddingVectorIndex;

namespace PuddingVectorIndexTests;

/// <summary>
/// Deterministic stand-in for the vector service (the port's whole point: the component is testable
/// without a service running). It records how it was called so batching can be asserted instead of
/// assumed, and it can be configured to misbehave (short batch, wrong dimension, unavailable) so the
/// builder's fail-closed checks have something to catch.
/// </summary>
internal sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    private readonly Func<string, float[]> _embed;
    private readonly int _dimensions;

    public StubEmbeddingProvider(
        int dimensions,
        Func<string, float[]>? embed = null,
        bool isAvailable = true,
        int maxContextTokens = 8192,
        bool isLocal = true,
        string providerId = "stub-provider",
        string modelId = "stub-model")
    {
        _dimensions = dimensions;
        _embed = embed ?? (_ => DefaultVector(dimensions));
        IsAvailable = isAvailable;
        MaxContextTokens = maxContextTokens;
        IsLocal = isLocal;
        ProviderId = providerId;
        ModelId = modelId;
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public bool IsAvailable { get; }

    public bool MaxContextTokensIsSet => MaxContextTokens > 0;

    public int MaxContextTokens { get; }

    public bool IsLocal { get; }

    public int BatchCalls { get; private set; }

    public int SingleCalls { get; private set; }

    public List<int> BatchSizes { get; } = [];

    /// <summary>When set, the batch call returns this many vectors regardless of the input.</summary>
    public int? TruncateBatchTo { get; init; }

    /// <summary>When set, every returned vector has this length regardless of the declared dimension.</summary>
    public int? ReturnDimensionOverride { get; init; }

    /// <summary>When set, the vector at this index of every batch is null.</summary>
    public int? NullVectorAtIndex { get; init; }

    public EmbeddingModelInfo Describe() =>
        new(ProviderId, ModelId, _dimensions, MaxContextTokens, IsLocal, IsAvailable);

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        SingleCalls++;
        return Task.FromResult(Adapted(_embed(text)));
    }

    public Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        BatchCalls++;
        BatchSizes.Add(texts.Count);

        var vectors = new float[TruncateBatchTo ?? texts.Count][];
        for (var i = 0; i < vectors.Length && i < texts.Count; i++)
        {
            vectors[i] = NullVectorAtIndex == i ? null! : Adapted(_embed(texts[i]));
        }

        return Task.FromResult(vectors);
    }

    /// <summary>A deterministic non-zero vector derived from the text, so two documents differ.</summary>
    public static float[] DefaultVector(int dimensions, string seed = "default")
    {
        var vector = new float[dimensions];
        var hash = 17;
        foreach (var character in seed)
            hash = unchecked(hash * 31 + character);

        for (var i = 0; i < dimensions; i++)
            vector[i] = 1f + ((hash + i * 7) % 13) / 13f;

        return vector;
    }

    /// <summary>
    /// A vector that is exactly <c>1</c> at the slot named by <paramref name="slot"/> and <c>0</c>
    /// elsewhere — an orthogonal basis, which makes "did document i get its own vector" decidable.
    /// </summary>
    public static float[] BasisVector(int dimensions, int slot)
    {
        var vector = new float[dimensions];
        vector[slot] = 1f;
        return vector;
    }

    private float[] Adapted(float[] vector)
    {
        if (ReturnDimensionOverride is not { } length || length == vector.Length)
            return vector;

        var resized = new float[length];
        Array.Copy(vector, resized, Math.Min(length, vector.Length));
        return resized;
    }
}
