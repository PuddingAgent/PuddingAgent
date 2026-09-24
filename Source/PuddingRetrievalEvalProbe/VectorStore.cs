using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingVectorIndex;

namespace PuddingRetrievalEvalProbe;

/// <summary>One stored chunk: provenance only. The text is not stored (see <see cref="VectorIndexEntry"/>).</summary>
internal sealed record VectorStoreEntry(
    string Id,
    string SourceFile,
    int StartLine,
    int EndLine,
    string Kind,
    float Boost);

/// <summary>
/// The on-disk sidecar of the vector store: everything except the numbers, plus the accounting the
/// four-axis report needs.
/// <para>
/// <see cref="Format"/> says what the vectors file actually contains — <c>float32</c> or <c>int8</c> —
/// so a reader never has to guess from the file name, and a store written by an older build (no such
/// field) is read as float32, which is what it was. <see cref="TierFilter"/> records the build-time tier
/// selection for the same reason: a report must not have to infer from its own file name what was
/// indexed.
/// </para>
/// </summary>
internal sealed record VectorStoreManifest(
    string Schema,
    string Route,
    int Dimensions,
    int Count,
    int BatchSize,
    int EmbeddingCalls,
    long EmbeddingMs,
    int? DeclaredDimensions,
    IReadOnlyList<VectorStoreEntry> Entries,
    string RecordedAtUtc,
    string? Note,
    string Format = VectorStore.FormatFloat32,
    string? TierFilter = null);

/// <summary>
/// Persists and reloads the in-memory vector index.
/// <para>
/// The vector component itself does no IO — it is pure logic — so the "index size" axis is measured
/// here, on the composition-root side, over exactly the bytes a deployed store would occupy.
/// </para>
/// <para>
/// Two formats are written from the same entries, and both are honest about what they are:
/// </para>
/// <list type="bullet">
/// <item><description><c>vectors.f32</c> — raw little-endian float32 rows. The smallest faithful
/// representation of "the vectors themselves", so an index-size comparison is not inflated by an
/// encoding choice. This is the U4-1b baseline format and its behaviour is unchanged.</description></item>
/// <item><description><c>vectors.i8</c> — one signed byte per component plus one float32 scale per row
/// (row = <c>Dimensions + 4</c> bytes). This is the format U4-3 measures: the codes are what a real
/// store would keep, and the scale is the only per-row overhead the symmetric scheme carries.
/// </description></item>
/// </list>
/// <para>
/// Reading is format-checked and fail-closed: a manifest that declares a format this probe does not
/// know, or a vectors file whose length does not match its declared row layout, is an error rather than
/// a best-effort decode. Both paths rebuild a float index through
/// <see cref="QuantizedVectorEntry.ToVectorEntry"/> (for int8), so the search side sees exactly what a
/// loaded store would see and the similarity rule is the one in <c>VectorMath</c> either way.
/// </para>
/// </summary>
internal static class VectorStore
{
    public const string Schema = "pudding-vector-store-v1";
    public const string DirectoryName = "vector-store";
    public const string VectorsFileName = "vectors.f32";
    public const string QuantizedVectorsFileName = "vectors.i8";
    public const string ManifestFileName = "manifest.json";

    /// <summary>Format tag for raw little-endian float32 rows (the U4-1b baseline).</summary>
    public const string FormatFloat32 = "float32";

    /// <summary>Format tag for symmetric absmax int8 codes plus one float32 scale per row.</summary>
    public const string FormatInt8 = "int8";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Bytes one int8 row occupies: one code per component plus the row's scale.</summary>
    public static long QuantizedRowBytes(int dimensions) => dimensions + sizeof(float);

    /// <summary>Writes the float32 store and returns the byte breakdown (the size axis).</summary>
    public static (long TotalBytes, long VectorBytes, long ManifestBytes) Write(
        string storeDirectory,
        InMemoryVectorIndex index,
        VectorStoreManifest manifest)
    {
        Directory.CreateDirectory(storeDirectory);

        var vectorsPath = Path.Combine(storeDirectory, VectorsFileName);
        var manifestPath = Path.Combine(storeDirectory, ManifestFileName);

        var buffer = new byte[(long)index.Count * index.Dimensions * sizeof(float)];
        var offset = 0;
        foreach (var entry in index.Entries)
        {
            var vector = entry.Vector.Span;
            for (var i = 0; i < vector.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), vector[i]);
                offset += sizeof(float);
            }
        }

        File.WriteAllBytes(vectorsPath, buffer);
        WriteManifest(manifestPath, manifest);

        var vectorBytes = new FileInfo(vectorsPath).Length;
        var manifestBytes = new FileInfo(manifestPath).Length;

        return (vectorBytes + manifestBytes, vectorBytes, manifestBytes);
    }

    /// <summary>
    /// Writes the int8 store: one row per entry, <c>Dimensions</c> codes followed by that row's scale.
    /// <para>
    /// The scale is stored per row rather than globally on purpose: absmax derives it from the vector
    /// itself, so two rows legitimately have different scales and a single store-wide scale would be a
    /// different (worse) quantiser pretending to be this one.
    /// </para>
    /// </summary>
    public static (long TotalBytes, long VectorBytes, long ManifestBytes) WriteQuantized(
        string storeDirectory,
        IReadOnlyList<QuantizedVectorEntry> entries,
        VectorStoreManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Dimensions <= 0)
            throw new InvalidOperationException(
                $"the manifest declares {manifest.Dimensions} dimensions; an int8 store needs a positive row width");

        if (entries.Count != manifest.Count)
            throw new InvalidOperationException(
                $"the manifest declares {manifest.Count} rows but {entries.Count} quantised entries were supplied; "
                + "a mismatched store would misalign every row after the gap");

        foreach (var entry in entries)
        {
            if (entry.Dimensions != manifest.Dimensions)
                throw new InvalidOperationException(
                    $"entry '{entry.Id}' has {entry.Dimensions} components but the manifest declares "
                    + $"{manifest.Dimensions}; rows of different widths cannot share one file layout");
        }

        Directory.CreateDirectory(storeDirectory);

        var vectorsPath = Path.Combine(storeDirectory, QuantizedVectorsFileName);
        var manifestPath = Path.Combine(storeDirectory, ManifestFileName);
        var rowBytes = QuantizedRowBytes(manifest.Dimensions);
        var buffer = new byte[(long)entries.Count * rowBytes];
        var offset = 0;

        foreach (var entry in entries)
        {
            var codes = entry.Vector.Codes.Span;
            for (var i = 0; i < codes.Length; i++)
                buffer[offset + i] = unchecked((byte)codes[i]);

            offset += codes.Length;
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), entry.Vector.Scale);
            offset += sizeof(float);
        }

        File.WriteAllBytes(vectorsPath, buffer);
        WriteManifest(manifestPath, manifest);

        var vectorBytes = new FileInfo(vectorsPath).Length;
        var manifestBytes = new FileInfo(manifestPath).Length;

        return (vectorBytes + manifestBytes, vectorBytes, manifestBytes);
    }

    /// <summary>Reloads a store, failing closed on a schema/format/dimension/row-count mismatch.</summary>
    public static (InMemoryVectorIndex Index, VectorStoreManifest Manifest, long Bytes) Read(string storeDirectory)
    {
        var manifestPath = Path.Combine(storeDirectory, ManifestFileName);

        if (!File.Exists(manifestPath))
            throw new InvalidOperationException(
                $"no vector store at {storeDirectory}; run --mode index --retriever vector first");

        var manifest = JsonSerializer.Deserialize<VectorStoreManifest>(File.ReadAllText(manifestPath), Options)
            ?? throw new InvalidOperationException($"vector store manifest at {manifestPath} is empty");

        if (!string.Equals(manifest.Schema, Schema, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"vector store at {storeDirectory} declares schema '{manifest.Schema}', this probe writes '{Schema}'");

        return manifest.Format switch
        {
            FormatFloat32 => ReadFloat32(storeDirectory, manifest),
            FormatInt8 => ReadInt8(storeDirectory, manifest),
            _ => throw new InvalidOperationException(
                $"vector store at {manifestPath} declares format '{manifest.Format}'; this probe knows "
                + $"'{FormatFloat32}' and '{FormatInt8}'. Refusing to guess how to decode it."),
        };
    }

    public static string VectorsFileNameFor(string format) => format switch
    {
        FormatFloat32 => VectorsFileName,
        FormatInt8 => QuantizedVectorsFileName,
        _ => throw new ArgumentException($"unknown vector store format '{format}'", nameof(format)),
    };

    private static (InMemoryVectorIndex Index, VectorStoreManifest Manifest, long Bytes) ReadFloat32(
        string storeDirectory,
        VectorStoreManifest manifest)
    {
        var vectorsPath = Path.Combine(storeDirectory, VectorsFileName);
        if (!File.Exists(vectorsPath))
            throw new InvalidOperationException(
                $"the manifest at {storeDirectory} declares float32 rows but {VectorsFileName} is missing");

        var bytes = File.ReadAllBytes(vectorsPath);
        var rowBytes = manifest.Dimensions * sizeof(float);
        var expected = (long)manifest.Count * rowBytes;

        if (bytes.LongLength != expected)
            throw new InvalidOperationException(
                $"vector store at {vectorsPath} holds {bytes.LongLength} bytes but the manifest declares "
                + $"{manifest.Count} rows of {manifest.Dimensions} float32 ({expected} bytes)");

        var index = new InMemoryVectorIndex(manifest.Dimensions, EmbeddingRoute.Parse(manifest.Route));

        for (var row = 0; row < manifest.Count; row++)
        {
            var vector = new float[manifest.Dimensions];
            var rowOffset = row * rowBytes;
            for (var i = 0; i < manifest.Dimensions; i++)
                vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(rowOffset + (i * sizeof(float))));

            index.Add(ToEntry(manifest, row, vector));
        }

        var totalBytes = new FileInfo(vectorsPath).Length + new FileInfo(Path.Combine(storeDirectory, ManifestFileName)).Length;
        return (index, manifest, totalBytes);
    }

    private static (InMemoryVectorIndex Index, VectorStoreManifest Manifest, long Bytes) ReadInt8(
        string storeDirectory,
        VectorStoreManifest manifest)
    {
        var vectorsPath = Path.Combine(storeDirectory, QuantizedVectorsFileName);
        if (!File.Exists(vectorsPath))
            throw new InvalidOperationException(
                $"the manifest at {storeDirectory} declares int8 rows but {QuantizedVectorsFileName} is missing");

        var bytes = File.ReadAllBytes(vectorsPath);
        var rowBytes = QuantizedRowBytes(manifest.Dimensions);
        var expected = (long)manifest.Count * rowBytes;

        if (bytes.LongLength != expected)
            throw new InvalidOperationException(
                $"vector store at {vectorsPath} holds {bytes.LongLength} bytes but the manifest declares "
                + $"{manifest.Count} rows of {manifest.Dimensions} int8 codes plus one float32 scale ({expected} bytes)");

        var index = new InMemoryVectorIndex(manifest.Dimensions, EmbeddingRoute.Parse(manifest.Route));

        for (var row = 0; row < manifest.Count; row++)
        {
            var rowOffset = (int)(row * rowBytes);
            var codes = new sbyte[manifest.Dimensions];
            for (var i = 0; i < manifest.Dimensions; i++)
                codes[i] = unchecked((sbyte)bytes[rowOffset + i]);

            var scale = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(rowOffset + manifest.Dimensions));

            // Dequantisation (and its refusal of an out-of-range code or a non-positive scale) belongs to
            // the component; the store only moves bytes.
            var quantised = new QuantizedVector(codes, scale);
            var entry = new QuantizedVectorEntry(
                manifest.Entries[row].Id,
                manifest.Entries[row].SourceFile,
                manifest.Entries[row].StartLine,
                manifest.Entries[row].EndLine,
                manifest.Entries[row].Kind,
                manifest.Entries[row].Boost,
                quantised);

            var vectorEntry = entry.ToVectorEntry();
            index.Add(vectorEntry);
        }

        var totalBytes = new FileInfo(vectorsPath).Length + new FileInfo(Path.Combine(storeDirectory, ManifestFileName)).Length;
        return (index, manifest, totalBytes);
    }

    private static VectorIndexEntry ToEntry(VectorStoreManifest manifest, int row, float[] vector)
    {
        var entry = manifest.Entries[row];
        return new VectorIndexEntry(
            entry.Id,
            entry.SourceFile,
            entry.StartLine,
            entry.EndLine,
            entry.Kind,
            entry.Boost,
            vector);
    }

    private static void WriteManifest(string manifestPath, VectorStoreManifest manifest) =>
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Options), new UTF8Encoding(false));
}
