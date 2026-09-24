namespace PuddingVectorIndex;

/// <summary>
/// One quantised vector plus the provenance a store needs to write it back and a reader needs to score
/// it (source file, line range, tier label, boost).
/// <para>
/// Deliberately a separate type from <see cref="VectorIndexEntry"/> rather than a flag on it: the
/// float32 entry keeps exactly the meaning it had (a search-side value), so adding a storage format
/// cannot silently change what an existing entry is. The bridge runs in one direction —
/// <see cref="ToVectorEntry"/> dequantises — which is the same direction a persisted store takes when it
/// is loaded: codes on disk, floats in memory, one similarity definition (<see cref="VectorMath.Cosine"/>).
/// </para>
/// </summary>
public sealed class QuantizedVectorEntry
{
    /// <param name="id">Stable identity of the chunk inside one store (source path plus line range).</param>
    /// <param name="vector">The quantised vector; its dimension is what the store writes per row.</param>
    public QuantizedVectorEntry(
        string id,
        string sourceFile,
        int startLine,
        int endLine,
        string kind,
        float boost,
        QuantizedVector vector)
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

        Id = id;
        SourceFile = sourceFile;
        StartLine = startLine;
        EndLine = endLine;
        Kind = kind ?? string.Empty;
        Boost = boost;
        Vector = vector;
    }

    /// <summary>Stable identity of the chunk inside one store.</summary>
    public string Id { get; }

    /// <summary>Source file the chunk came from — the field the evaluation metrics match on.</summary>
    public string SourceFile { get; }

    /// <summary>First source line covered (1-based, inclusive).</summary>
    public int StartLine { get; }

    /// <summary>Last source line covered (1-based, inclusive).</summary>
    public int EndLine { get; }

    /// <summary>Tier label kept for auditing (e.g. <c>Outline</c> / <c>DocComment</c> / <c>CodeText</c>).</summary>
    public string Kind { get; }

    /// <summary>Tier weight recorded by the caller. Not applied here — cosine ranking is unweighted.</summary>
    public float Boost { get; }

    /// <summary>The quantised vector (codes plus scale).</summary>
    public QuantizedVector Vector { get; }

    /// <summary>Dimension of the stored vector.</summary>
    public int Dimensions => Vector.Dimensions;

    /// <summary>Bytes this row occupies in a store: <see cref="QuantizedVector.StorageBytes"/>.</summary>
    public int StorageBytes => Vector.StorageBytes;

    /// <summary>
    /// The float32 entry this row reconstructs to. This is the only conversion between the storage shape
    /// and the search shape, so a measurement cannot end up comparing two different similarity rules.
    /// </summary>
    public VectorIndexEntry ToVectorEntry() =>
        new(Id, SourceFile, StartLine, EndLine, Kind, Boost, Vector.Dequantize());
}
