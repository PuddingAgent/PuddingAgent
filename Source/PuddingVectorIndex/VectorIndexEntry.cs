namespace PuddingVectorIndex;

/// <summary>
/// One indexed vector plus the provenance a report needs (which source file/line range, which tier).
/// <para>
/// The vector is copied on construction so a caller cannot mutate an entry after it was added to an
/// index — a silently mutated vector would invalidate every previously computed ranking.
/// </para>
/// <para>
/// The chunk <b>text</b> is deliberately not stored: this store is what the "index size" axis
/// measures, and keeping the text would conflate "vector store" with "document store". A caller that
/// needs the text keeps it next to the entry (the probe does).
/// </para>
/// </summary>
public sealed class VectorIndexEntry
{
    private readonly float[] _vector;

    public VectorIndexEntry(
        string id,
        string sourceFile,
        int startLine,
        int endLine,
        string kind,
        float boost,
        float[] vector)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("entry id must not be blank", nameof(id));

        if (string.IsNullOrWhiteSpace(sourceFile))
            throw new ArgumentException("entry source file must not be blank", nameof(sourceFile));

        if (startLine < 1)
            throw new ArgumentOutOfRangeException(nameof(startLine), startLine, "line numbers are 1-based");

        if (endLine < startLine)
            throw new ArgumentOutOfRangeException(nameof(endLine), endLine, "end line must not precede start line");

        ArgumentNullException.ThrowIfNull(vector);

        if (vector.Length == 0)
            throw new ArgumentException("vector must not be empty", nameof(vector));

        Id = id;
        SourceFile = sourceFile;
        StartLine = startLine;
        EndLine = endLine;
        Kind = kind ?? string.Empty;
        Boost = boost;
        _vector = (float[])vector.Clone();
    }

    /// <summary>Stable identity of the chunk inside one store (source path plus line range).</summary>
    public string Id { get; }

    /// <summary>Source file the chunk came from — the only field the evaluation metrics match on.</summary>
    public string SourceFile { get; }

    /// <summary>First source line covered (1-based, inclusive).</summary>
    public int StartLine { get; }

    /// <summary>Last source line covered (1-based, inclusive).</summary>
    public int EndLine { get; }

    /// <summary>Tier label kept for auditing (e.g. <c>Outline</c> / <c>DocComment</c> / <c>CodeText</c>).</summary>
    public string Kind { get; }

    /// <summary>Tier weight recorded by the caller. Not applied here — cosine ranking is unweighted.</summary>
    public float Boost { get; }

    /// <summary>The stored vector (a defensive copy of what the caller supplied).</summary>
    public ReadOnlyMemory<float> Vector => _vector;

    /// <summary>Length of the stored vector.</summary>
    public int Dimensions => _vector.Length;

    /// <summary>Read-only view for the search loop.</summary>
    internal ReadOnlySpan<float> Span => _vector;
}

/// <summary>
/// One ranked hit. <see cref="Rank"/> is 0-based and is exactly the position in the returned list, so
/// a caller never has to re-derive it (and cannot derive it differently).
/// </summary>
public sealed record VectorSearchResult(
    string Id,
    string SourceFile,
    int StartLine,
    int EndLine,
    string Kind,
    double Score,
    int Rank);
