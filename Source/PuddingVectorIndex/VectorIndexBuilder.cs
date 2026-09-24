using System.Diagnostics;

namespace PuddingVectorIndex;

/// <summary>
/// One chunk waiting to be embedded: the text plus the provenance the index must keep.
/// <para>
/// The chunking component's own model cannot be reused here (that would be a dependency on
/// <c>PuddingIndexChunking</c>), and neither can the full-text index's document shape (that would be a
/// dependency on <c>PuddingFullTextIndex</c>). The caller maps its chunk into this shape, which keeps
/// the vector component independent of both.
/// </para>
/// </summary>
public sealed record VectorDocument
{
    public VectorDocument(
        string id,
        string sourceFile,
        int startLine,
        int endLine,
        string kind,
        float boost,
        string text)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("document id must not be blank", nameof(id));

        if (string.IsNullOrWhiteSpace(sourceFile))
            throw new ArgumentException("document source file must not be blank", nameof(sourceFile));

        if (startLine < 1)
            throw new ArgumentOutOfRangeException(nameof(startLine), startLine, "line numbers are 1-based");

        if (endLine < startLine)
            throw new ArgumentOutOfRangeException(nameof(endLine), endLine, "end line must not precede start line");

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("document text must not be blank", nameof(text));

        Id = id;
        SourceFile = sourceFile;
        StartLine = startLine;
        EndLine = endLine;
        Kind = kind ?? string.Empty;
        Boost = boost;
        Text = text;
    }

    public string Id { get; }

    public string SourceFile { get; }

    public int StartLine { get; }

    public int EndLine { get; }

    public string Kind { get; }

    public float Boost { get; }

    /// <summary>The exact text handed to the provider — never trimmed or re-normalised.</summary>
    public string Text { get; }
}

/// <summary>
/// How a build runs: batching, which tiers may become vectors, and whether the stored rows are
/// quantised. Every option defaults to the U4-1b behaviour (no filter, no quantisation), so a caller
/// that passes only a batch size still gets the identical index.
/// </summary>
public sealed record VectorIndexBuildOptions
{
    /// <param name="batchSize">
    /// Documents per <see cref="IEmbeddingProvider.EmbedBatchAsync"/> call; must be &gt;= 1.
    /// </param>
    /// <param name="tierFilter">
    /// Which <see cref="VectorDocument.Kind"/> values may become vectors; <c>null</c> (the default) means
    /// every tier. Applied <b>before</b> embedding, so an excluded document costs no round trip — and,
    /// because it never reaches the index, the entries that remain are untouched (see
    /// <see cref="VectorTierFilter"/>).
    /// </param>
    /// <param name="quantize">
    /// When true, each vector is stored as symmetric int8 codes and the index is filled with the
    /// <b>round-tripped</b> values. See <see cref="VectorIndexBuilder.BuildAsync"/> for why that is the
    /// only honest way to measure what the format costs.
    /// </param>
    public VectorIndexBuildOptions(
        int batchSize = 32,
        VectorTierFilter? tierFilter = null,
        bool quantize = false)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batch size must be >= 1");

        BatchSize = batchSize;
        TierFilter = tierFilter;
        Quantize = quantize;
    }

    public int BatchSize { get; }

    /// <summary>Tiers allowed to become vectors; <c>null</c> = every tier (the default, unchanged behaviour).</summary>
    public VectorTierFilter? TierFilter { get; }

    /// <summary>Whether vectors are stored as int8 codes. Default false.</summary>
    public bool Quantize { get; }
}

/// <summary>
/// Result of one build. <see cref="EmbeddingCalls"/> and <see cref="EmbeddingMs"/> are reported
/// separately from the total build time on purpose: the index-speed axis must show how much of the
/// wall time was the vector service and how much was everything else, and the number of round trips is
/// what batching is judged by.
/// <para>
/// <see cref="SkippedByTierCount"/> and <see cref="EmptyReason"/> are the layered-selection accounting:
/// a build that filtered everything out is reported as such rather than as "an empty index", because
/// the second reading is a claim about the scope and the first one is a claim about the filter.
/// </para>
/// </summary>
public sealed record VectorIndexBuildResult(
    InMemoryVectorIndex Index,
    int DocumentCount,
    int EmbeddingCalls,
    long EmbeddingMs,
    int Dimensions,
    int SkippedByTierCount = 0,
    string? EmptyReason = null)
{
    /// <summary>
    /// The quantised rows, in index order; empty unless the build was asked to quantise. A store writes
    /// these (codes plus one scale per row) while the search side keeps using <see cref="Index"/>, so
    /// the two halves of the pipeline cannot drift apart.
    /// </summary>
    public IReadOnlyList<QuantizedVectorEntry> QuantizedEntries { get; init; } = [];

    /// <summary>Vectors actually in the index; equal to <c>DocumentCount - SkippedByTierCount</c> once the build completes.</summary>
    public int IndexedCount => Index.Count;
}

/// <summary>
/// Turns documents into an <see cref="InMemoryVectorIndex"/> through the
/// <see cref="IEmbeddingProvider"/> port.
/// <para>
/// This is where the port's contract is enforced rather than trusted: the number of vectors must equal
/// the number of inputs, each vector must have the declared length, and each must have a direction.
/// A provider that returns fewer vectors than asked (a truncated batch) would otherwise silently shift
/// every subsequent document onto the wrong vector — an indexing bug that looks like a retrieval
/// quality result.
/// </para>
/// <para>
/// Two optional mechanisms sit on top of that contract, both of them off by default so the U4-1b
/// behaviour is preserved exactly: <b>layered selection</b> (<see cref="VectorIndexBuildOptions.TierFilter"/>
/// decides, before any embedding, which tiers may become vectors) and <b>int8 quantisation</b>
/// (<see cref="VectorIndexBuildOptions.Quantize"/> stores codes and fills the index with the
/// round-tripped values, which is what makes the format's quality cost measurable rather than zero).
/// </para>
/// </summary>
public static class VectorIndexBuilder
{
    public static async Task<VectorIndexBuildResult> BuildAsync(
        IEmbeddingProvider provider,
        IReadOnlyList<VectorDocument> documents,
        VectorIndexBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(documents);

        var buildOptions = options ?? new VectorIndexBuildOptions();
        var batchSize = buildOptions.BatchSize;

        var info = provider.Describe()
            ?? throw new InvalidOperationException("the embedding provider returned no model description");

        if (!info.IsAvailable)
            throw new InvalidOperationException(
                $"embedding route '{info.Route}' is not available (provider disabled, model not registered, "
                + "or endpoint unresolved); refusing to build an index that could not be filled");

        if (info.Dimensions <= 0)
            throw new InvalidOperationException(
                $"embedding route '{info.Route}' declares {info.Dimensions} dimensions; a usable provider must "
                + "declare its vector length before the index can be built");

        // ── layered selection: the tier filter runs at BUILD time ───────────────────────────────
        // A document the filter rejects is never embedded and never added. Filtering afterwards (by
        // trimming the finished index) would have paid for every embedding anyway and would leave the
        // surviving entries indistinguishable from an unfiltered build — a shape that makes "layered"
        // unmeasurable. Keeping the decision here also means one place decides it, before any IO.
        var selected = new List<VectorDocument>(documents.Count);
        foreach (var document in documents)
        {
            if (buildOptions.TierFilter is null || buildOptions.TierFilter.Includes(document.Kind))
                selected.Add(document);
        }

        var skippedByTier = documents.Count - selected.Count;

        // An empty index has two very different causes, and they must not share a sentence: "the input
        // was empty" is a fact about the scope, "the tier filter rejected everything" is a fact about
        // the filter (ADR-089 §2.3 — filtered-empty is not really-empty).
        var emptyReason = selected.Count > 0
            ? null
            : documents.Count == 0
                ? "the build was handed no documents at all (an empty input, not a filter decision)"
                : $"all {documents.Count} documents were excluded by the tier filter "
                  + $"[{buildOptions.TierFilter!.Description}]; an empty index here is the filter's result, "
                  + "not evidence that the scope has nothing to index";

        var index = new InMemoryVectorIndex(info.Dimensions, info.Route);
        var quantizedEntries = new List<QuantizedVectorEntry>(buildOptions.Quantize ? selected.Count : 0);
        var embeddingCalls = 0;
        long embeddingMs = 0;

        for (var offset = 0; offset < selected.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(batchSize, selected.Count - offset);
            var texts = new string[count];
            for (var i = 0; i < count; i++)
                texts[i] = selected[offset + i].Text;

            var stopwatch = Stopwatch.StartNew();
            var vectors = await provider
                .EmbedBatchAsync(texts, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            embeddingCalls++;
            embeddingMs += stopwatch.ElapsedMilliseconds;

            if (vectors is null || vectors.Length != count)
                throw new InvalidOperationException(
                    $"embedding route '{info.Route}' returned {(vectors?.Length ?? 0)} vectors for a batch of "
                    + $"{count}; a short batch would misalign every following document");

            for (var i = 0; i < count; i++)
            {
                var document = selected[offset + i];

                var vector = vectors[i]
                    ?? throw new InvalidOperationException(
                        $"embedding route '{info.Route}' returned a null vector for document '{document.Id}'");

                if (vector.Length != info.Dimensions)
                    throw new InvalidOperationException(
                        $"embedding route '{info.Route}' declared {info.Dimensions} dimensions but returned "
                        + $"{vector.Length} for document '{document.Id}'");

                if (!buildOptions.Quantize)
                {
                    index.Add(new VectorIndexEntry(
                        document.Id,
                        document.SourceFile,
                        document.StartLine,
                        document.EndLine,
                        document.Kind,
                        document.Boost,
                        vector));
                    continue;
                }

                // Quantise, then index the ROUND-TRIPPED vector rather than the original. A store keeps
                // only codes, so this is what a store loaded from disk would search; indexing the
                // untouched float instead would report a quantisation cost of exactly zero, which is the
                // one answer this measurement must not be able to produce.
                var quantised = VectorQuantizer.Quantize(vector);
                index.Add(new VectorIndexEntry(
                    document.Id,
                    document.SourceFile,
                    document.StartLine,
                    document.EndLine,
                    document.Kind,
                    document.Boost,
                    quantised.Dequantize()));

                quantizedEntries.Add(new QuantizedVectorEntry(
                    document.Id,
                    document.SourceFile,
                    document.StartLine,
                    document.EndLine,
                    document.Kind,
                    document.Boost,
                    quantised));
            }
        }

        return new VectorIndexBuildResult(
            index,
            documents.Count,
            embeddingCalls,
            embeddingMs,
            info.Dimensions,
            skippedByTier,
            emptyReason)
        {
            QuantizedEntries = quantizedEntries,
        };
    }
}
