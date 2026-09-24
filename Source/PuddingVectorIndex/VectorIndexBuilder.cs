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

/// <summary>How many documents go into one provider round trip.</summary>
public sealed record VectorIndexBuildOptions
{
    /// <param name="batchSize">
    /// Documents per <see cref="IEmbeddingProvider.EmbedBatchAsync"/> call; must be &gt;= 1.
    /// </param>
    public VectorIndexBuildOptions(int batchSize = 32)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batch size must be >= 1");

        BatchSize = batchSize;
    }

    public int BatchSize { get; }
}

/// <summary>
/// Result of one build. <see cref="EmbeddingCalls"/> and <see cref="EmbeddingMs"/> are reported
/// separately from the total build time on purpose: the index-speed axis must show how much of the
/// wall time was the vector service and how much was everything else, and the number of round trips is
/// what batching is judged by.
/// </summary>
public sealed record VectorIndexBuildResult(
    InMemoryVectorIndex Index,
    int DocumentCount,
    int EmbeddingCalls,
    long EmbeddingMs,
    int Dimensions);

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

        var batchSize = (options ?? new VectorIndexBuildOptions()).BatchSize;

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

        var index = new InMemoryVectorIndex(info.Dimensions, info.Route);
        var embeddingCalls = 0;
        long embeddingMs = 0;

        for (var offset = 0; offset < documents.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(batchSize, documents.Count - offset);
            var texts = new string[count];
            for (var i = 0; i < count; i++)
                texts[i] = documents[offset + i].Text;

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
                var vector = vectors[i]
                    ?? throw new InvalidOperationException(
                        $"embedding route '{info.Route}' returned a null vector for document '{documents[offset + i].Id}'");

                if (vector.Length != info.Dimensions)
                    throw new InvalidOperationException(
                        $"embedding route '{info.Route}' declared {info.Dimensions} dimensions but returned "
                        + $"{vector.Length} for document '{documents[offset + i].Id}'");

                var document = documents[offset + i];
                index.Add(new VectorIndexEntry(
                    document.Id,
                    document.SourceFile,
                    document.StartLine,
                    document.EndLine,
                    document.Kind,
                    document.Boost,
                    vector));
            }
        }

        return new VectorIndexBuildResult(index, documents.Count, embeddingCalls, embeddingMs, info.Dimensions);
    }
}
