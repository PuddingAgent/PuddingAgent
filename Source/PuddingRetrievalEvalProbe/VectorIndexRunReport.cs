using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Machine-readable record of one vector/hybrid index build (the speed and size halves of the
/// four-axis comparison, with the embedding split out).
/// <para>
/// Kept separate from <see cref="IndexRunReport"/> on purpose: that type belongs to the U4-1a full-text
/// matrix and is left untouched (既有行为只增不改). Every value a given retriever cannot report stays
/// <c>null</c> rather than being filled with a plausible-looking zero.
/// </para>
/// </summary>
internal sealed record VectorIndexRunReport
{
    public required string Retriever { get; init; }

    public required string Strategy { get; init; }

    public required string Tiers { get; init; }

    public required string Filter { get; init; }

    public required string Scope { get; init; }

    public required string ScopeLabel { get; init; }

    public required string IndexRoot { get; init; }

    public required string IndexDirectory { get; init; }

    /// <summary>Total bytes of the index root on disk — the "index size" axis (Lucene + vector store).</summary>
    public required long IndexBytes { get; init; }

    public required int IndexFileCount { get; init; }

    public int? SourceFiles { get; init; }

    /// <summary>Lucene documents written; <c>null</c> when this retriever has no full-text side.</summary>
    public int? LuceneDocuments { get; init; }

    /// <summary>Chunk documents embedded; the vector side's document count.</summary>
    public int? VectorDocuments { get; init; }

    public long? IndexedTextChars { get; init; }

    public int? RawTokens { get; init; }

    public int? RemovedAsShort { get; init; }

    public int? RemovedAsStopWord { get; init; }

    public int? IndexedTokens { get; init; }

    public IReadOnlyDictionary<string, int>? ChunksPerTier { get; init; }

    /// <summary>Walk + outline + chunk + filter (the same corpus both retrievers consume).</summary>
    public long? CorpusMs { get; init; }

    public long? LuceneWriteMs { get; init; }

    public long? EngineMs { get; init; }

    // ---- vector side ----

    public string? EmbeddingRoute { get; init; }

    public string? EmbeddingEndpoint { get; init; }

    public int? EmbeddingDeclaredDimensions { get; init; }

    public int? EmbeddingObservedDimensions { get; init; }

    public bool? EmbeddingIsLocal { get; init; }

    public int? EmbeddingMaxContextTokens { get; init; }

    public double? EmbeddingPricePer1MInputTokens { get; init; }

    public string? ProvidersConfigPath { get; init; }

    public int? EmbeddingBatchSize { get; init; }

    /// <summary>Round trips issued while building the index (the number batching is judged by).</summary>
    public int? IndexEmbeddingCalls { get; init; }

    /// <summary>Wall time inside those round trips only — the embedding part of the build.</summary>
    public long? IndexEmbeddingMs { get; init; }

    public int? IndexEmbeddedTexts { get; init; }

    /// <summary>Round trips spent on the reachability preflight (before any chunk is embedded).</summary>
    public int? PreflightCalls { get; init; }

    public long? PreflightMs { get; init; }

    /// <summary>Prompt tokens the service reported across the whole index build; <c>null</c> when unreported.</summary>
    public long? IndexPromptTokens { get; init; }

    public long? VectorStoreBytes { get; init; }

    public long? VectorVectorsBytes { get; init; }

    public long? VectorManifestBytes { get; init; }

    /// <summary>What the vectors file actually contains: <c>float32</c> or <c>int8</c> (U4-3).</summary>
    public string? VectorFormat { get; init; }

    /// <summary>The build-time tier switch as requested (<c>all</c> / <c>p0</c>); <c>null</c> for non-vector retrievers.</summary>
    public string? VectorTierFilter { get; init; }

    /// <summary>The tier labels the filter actually accepted, as the component reports them (e.g. <c>Outline</c>).</summary>
    public string? VectorTierFilterDescription { get; init; }

    /// <summary>Blocks the builder was given, before the tier filter ran.</summary>
    public int? VectorDocumentsConsidered { get; init; }

    /// <summary>Blocks the tier filter removed <b>before</b> embedding — the saving layered selection buys.</summary>
    public int? VectorDocumentsSkippedByTier { get; init; }

    /// <summary>Vectors-file bytes divided by indexed rows — the per-row cost question 1 of U4-3 asks.</summary>
    public double? VectorBytesPerDocument { get; init; }

    /// <summary>Non-null when the build produced an empty index, with the cause (empty input vs tier filter).</summary>
    public string? VectorEmptyReason { get; init; }

    public long? VectorStoreWriteMs { get; init; }

    /// <summary>Hybrid only: the RRF configuration that produced the fused ranking.</summary>
    public int? RrfK { get; init; }

    public double? RrfWeightFullText { get; init; }

    public double? RrfWeightVector { get; init; }

    public int? FusionDepth { get; init; }

    /// <summary>Whole index step as measured by the harness.</summary>
    public required long BuildMs { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public required string RecordedAtUtc { get; init; }

    public string? Note { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Writes the report as UTF-8 without BOM, creating the directory when needed.</summary>
    public static void Write(string path, VectorIndexRunReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(full, JsonSerializer.Serialize(report, Options), new UTF8Encoding(false));
    }
}

/// <summary>
/// Measure-side record: what the query embedding cost, and with which fusion parameters the numbers were
/// produced. Written next to the evaluation report so "vector latency" can be decomposed afterwards.
/// </summary>
internal sealed record ProbeRunSidecar
{
    public required string Retriever { get; init; }

    public required string Probe { get; init; }

    public required string Scope { get; init; }

    public required string ScopeLabel { get; init; }

    public required string SetPath { get; init; }

    public required int CaseCount { get; init; }

    public required int WarmupRepetitions { get; init; }

    public required int MeasuredRepetitions { get; init; }

    public required int MaxResults { get; init; }

    public string? EmbeddingRoute { get; init; }

    public string? EmbeddingEndpoint { get; init; }

    public bool? EmbeddingIsLocal { get; init; }

    public int? EmbeddingDimensions { get; init; }

    public int? VectorEntryCount { get; init; }

    public long? VectorStoreBytes { get; init; }

    /// <summary>What the store the query side searched actually contains (U4-3): <c>float32</c> or <c>int8</c>.</summary>
    public string? VectorFormat { get; init; }

    /// <summary>The build-time tier filter the store recorded, when it recorded one.</summary>
    public string? VectorTierFilter { get; init; }

    /// <summary>Total calls the query-side embedding made during the measured run (cold + warm-up + measured).</summary>
    public int? QueryEmbeddingCalls { get; init; }

    public int? QueryEmbeddedTexts { get; init; }

    public long? QueryEmbeddingMs { get; init; }

    public int? QueryEmbeddingFailures { get; init; }

    public long? QueryPromptTokens { get; init; }

    public int? RrfK { get; init; }

    public double? RrfWeightFullText { get; init; }

    public double? RrfWeightVector { get; init; }

    public int? FusionDepth { get; init; }

    /// <summary>Honest cost line: local inference is 0; a remote route would have to list tokens and money.</summary>
    public double? EstimatedCostUsd { get; init; }

    public string? CostNote { get; init; }

    public required string RecordedAtUtc { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Write(string path, ProbeRunSidecar sidecar)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sidecar);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(full, JsonSerializer.Serialize(sidecar, Options), new UTF8Encoding(false));
    }
}
