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
    string? Note);

/// <summary>
/// Persists and reloads the in-memory vector index.
/// <para>
/// The vector component itself does no IO — it is pure logic — so the "index size" axis is measured
/// here, on the composition-root side, over exactly the bytes a deployed store would occupy
/// (<c>vectors.f32</c> = raw little-endian float32 rows, <c>manifest.json</c> = provenance).
/// </para>
/// <para>
/// Storing raw float32 (rather than JSON numbers or base64) is deliberate: it is the smallest honest
/// representation of "the vectors themselves", so the reported size is not inflated by an encoding
/// choice. Quantisation is <b>not</b> applied — the comparison must measure the embedding, not a
/// compression scheme.
/// </para>
/// </summary>
internal static class VectorStore
{
    public const string Schema = "pudding-vector-store-v1";
    public const string DirectoryName = "vector-store";
    public const string VectorsFileName = "vectors.f32";
    public const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes the store and returns the byte breakdown (the size axis).</summary>
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
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Options), new UTF8Encoding(false));

        var vectorBytes = new FileInfo(vectorsPath).Length;
        var manifestBytes = new FileInfo(manifestPath).Length;

        return (vectorBytes + manifestBytes, vectorBytes, manifestBytes);
    }

    /// <summary>Reloads a store, failing closed on a schema/dimension/row-count mismatch.</summary>
    public static (InMemoryVectorIndex Index, VectorStoreManifest Manifest, long Bytes) Read(string storeDirectory)
    {
        var vectorsPath = Path.Combine(storeDirectory, VectorsFileName);
        var manifestPath = Path.Combine(storeDirectory, ManifestFileName);

        if (!File.Exists(manifestPath) || !File.Exists(vectorsPath))
            throw new InvalidOperationException(
                $"no vector store at {storeDirectory}; run --mode index --retriever vector first");

        var manifest = JsonSerializer.Deserialize<VectorStoreManifest>(File.ReadAllText(manifestPath), Options)
            ?? throw new InvalidOperationException($"vector store manifest at {manifestPath} is empty");

        if (!string.Equals(manifest.Schema, Schema, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"vector store at {storeDirectory} declares schema '{manifest.Schema}', this probe writes '{Schema}'");

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
                vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(rowOffset + i * sizeof(float)));

            var entry = manifest.Entries[row];
            index.Add(new VectorIndexEntry(
                entry.Id,
                entry.SourceFile,
                entry.StartLine,
                entry.EndLine,
                entry.Kind,
                entry.Boost,
                vector));
        }

        var totalBytes = new FileInfo(vectorsPath).Length + new FileInfo(manifestPath).Length;
        return (index, manifest, totalBytes);
    }
}
