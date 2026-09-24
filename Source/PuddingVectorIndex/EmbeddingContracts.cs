namespace PuddingVectorIndex;

/// <summary>
/// Which provider/model an embedding came from, written the way the user states it: "provider/model"
/// (ADR-089 索引服务与库管理 §1 — "增强索引需在配置文件中定义使用的索引服务商和模型名称").
/// <para>
/// This is a <b>value</b>, not a service locator: the component records which route produced the
/// vectors so a reader can tell "these vectors are not comparable with those" — it never resolves the
/// route itself. Resolving <see cref="EmbeddingRoute"/> into a live provider is the composition root's
/// job (the probe, in the evaluation harness).
/// </para>
/// </summary>
/// <param name="ProviderId">Resource-pool provider id, e.g. <c>lmstudio-local</c>.</param>
/// <param name="ModelId">Model id inside that provider, e.g. <c>text-embedding-qwen3-embedding-0.6b</c>.</param>
public readonly record struct EmbeddingRoute(string ProviderId, string ModelId)
{
    /// <summary>
    /// Parses the user-visible <c>"provider/model"</c> string. The split is at the <b>first</b> slash
    /// because model ids may contain slashes themselves (e.g. <c>qwen/qwen3.6-35b-a3b</c>).
    /// Fail-closed: a blank provider or blank model is an error, never a half-filled route.
    /// </summary>
    public static EmbeddingRoute Parse(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
            throw new ArgumentException("embedding route must not be blank", nameof(route));

        var trimmed = route.Trim();
        var separator = trimmed.IndexOf('/');
        if (separator <= 0 || separator == trimmed.Length - 1)
            throw new ArgumentException($"embedding route '{route}' must be 'provider/model'", nameof(route));

        var providerId = trimmed[..separator].Trim();
        var modelId = trimmed[(separator + 1)..].Trim();

        if (providerId.Length == 0 || modelId.Length == 0)
            throw new ArgumentException($"embedding route '{route}' must be 'provider/model'", nameof(route));

        return new EmbeddingRoute(providerId, modelId);
    }

    /// <summary>Round-trips back to the <c>"provider/model"</c> string used in configuration and reports.</summary>
    public override string ToString() => $"{ProviderId}/{ModelId}";
}

/// <summary>
/// What a vector service can say about itself (ADR-089 §1: 维度 / 最大上下文 token / 是否本地 / 可用性).
/// <para>
/// <see cref="IsAvailable"/> is a <b>configuration-level</b> statement (provider enabled, model
/// registered, endpoint resolved) — it is not a liveness probe. Liveness is established by actually
/// calling the provider; that is why <see cref="VectorIndexBuilder"/> treats
/// <c>IsAvailable == false</c> as a hard error instead of quietly producing an empty index.
/// </para>
/// <para>
/// <see cref="Dimensions"/> may be <c>0</c> when the provider could not declare one; a usable
/// description must be <c>&gt; 0</c> and the builder enforces that on the first build.
/// </para>
/// </summary>
public sealed record EmbeddingModelInfo
{
    public EmbeddingModelInfo(
        string providerId,
        string modelId,
        int dimensions,
        int maxContextTokens,
        bool isLocal,
        bool isAvailable)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("provider id must not be blank", nameof(providerId));

        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("model id must not be blank", nameof(modelId));

        if (dimensions < 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "dimensions must not be negative");

        if (maxContextTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(maxContextTokens), maxContextTokens, "max context tokens must not be negative");

        ProviderId = providerId.Trim();
        ModelId = modelId.Trim();
        Dimensions = dimensions;
        MaxContextTokens = maxContextTokens;
        IsLocal = isLocal;
        IsAvailable = isAvailable;
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    /// <summary>Vector length this model produces; <c>0</c> means "not declared".</summary>
    public int Dimensions { get; }

    /// <summary>Longest input the model accepts, in tokens; <c>0</c> means "not declared".</summary>
    public int MaxContextTokens { get; }

    /// <summary>True when the model runs on this machine (no remote tokens, no per-token price).</summary>
    public bool IsLocal { get; }

    /// <summary>Configuration-level availability — see the type remarks.</summary>
    public bool IsAvailable { get; }

    /// <summary>The route this description belongs to.</summary>
    public EmbeddingRoute Route => new(ProviderId, ModelId);
}

/// <summary>
/// The single port through which the vector index observes a vector service (ADR-089 §1).
/// <para>
/// Same shape as <c>ISearchProbe</c> in the retrieval-evaluation component: the leaf depends on the
/// port and an external adapter supplies the concrete provider, so swapping "which provider/model" is
/// a wiring decision, never a change inside the component. Implementations own HTTP/credentials; the
/// component only ever sees floats.
/// </para>
/// <para>
/// Deliberately <b>not</b> the pre-existing <c>PuddingCode.Abstractions.IEmbeddingService</c>: that
/// contract fixes the provider inside its implementation and is already consumed by the memory domain
/// (<c>SessionChunkIndexer</c>, <c>EmbeddingGenerationHook</c>), so changing it would punch through the
/// memory layer. This port is additive and route-aware.
/// </para>
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Describes the model behind this provider. Must be side-effect free.</summary>
    EmbeddingModelInfo Describe();

    /// <summary>Embeds one text. The returned vector's length is the model's dimension.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds several texts in one round trip, <b>preserving input order</b> (index i of the result
    /// belongs to index i of <paramref name="texts"/>). Batching is the reason the harness can embed a
    /// thousand chunks at all: a single local embed costs ~390 ms while a batched one costs ~30 ms/item.
    /// </summary>
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
