using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Machine-readable record of one index build: the size/speed half of the four-axis comparison
/// (index time and index size), plus the token accounting that shows what the C2 filter removed.
/// <para>
/// Written only when the caller asks for it (<c>--index-json &lt;path&gt;</c>), so replaying an existing
/// baseline command writes exactly the files it wrote before. Values that a given strategy cannot
/// report stay <c>null</c> instead of being filled with a plausible-looking zero.
/// </para>
/// </summary>
internal sealed record IndexRunReport
{
    public required string Strategy { get; init; }

    /// <summary>Tier selection (<c>p0p1p2</c> / <c>p0p1</c> / <c>p0</c>), <c>n/a</c> for the plain strategy.</summary>
    public required string Tiers { get; init; }

    /// <summary>Which C2 rules were on (<c>both</c> / <c>none</c> / <c>length</c> / <c>stopwords</c>), <c>n/a</c> for plain.</summary>
    public required string Filter { get; init; }

    public required string Scope { get; init; }

    public required string ScopeLabel { get; init; }

    public required string IndexRoot { get; init; }

    public required string IndexDirectory { get; init; }

    /// <summary>Total bytes of the index directory on disk (the "index size" axis).</summary>
    public required long IndexBytes { get; init; }

    public required int IndexFileCount { get; init; }

    public int? SourceFiles { get; init; }

    /// <summary>Lucene documents written; <c>null</c> for the plain strategy (the engine reports files, not documents).</summary>
    public int? Documents { get; init; }

    public long? IndexedTextChars { get; init; }

    /// <summary>Tokens in the walked raw text — the "before filtering" denominator.</summary>
    public int? RawTokens { get; init; }

    public int? RemovedAsShort { get; init; }

    public int? RemovedAsStopWord { get; init; }

    /// <summary>Tokens present in the documents that were actually indexed (after filtering + tier selection).</summary>
    public int? IndexedTokens { get; init; }

    public IReadOnlyDictionary<string, int>? ChunksPerTier { get; init; }

    /// <summary>Walk + outline + chunk + filter.</summary>
    public long? CorpusMs { get; init; }

    /// <summary>Wall time of the whole index step (corpus + Lucene write).</summary>
    public required long BuildMs { get; init; }

    /// <summary>Time reported by the engine's own index writer.</summary>
    public long? EngineMs { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public required string RecordedAtUtc { get; init; }

    /// <summary>Free-form honesty note for anything the numbers cannot express.</summary>
    public string? Note { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Writes the report as UTF-8 without BOM, creating the directory when needed.</summary>
    public static void Write(string path, IndexRunReport report)
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

/// <summary>Small helpers shared by the index modes: directory measurement and pattern normalisation.</summary>
internal static class IndexProbeFiles
{
    /// <summary>Total bytes and file count under an index root (the root holds one hash directory per scope).</summary>
    public static (long Bytes, int Files, string Directory) MeasureIndexDirectory(string indexRoot)
    {
        var root = Path.GetFullPath(indexRoot);
        if (!Directory.Exists(root))
            return (0, 0, root);

        var directories = Directory.GetDirectories(root);
        long bytes = 0;
        var files = 0;
        var measured = new List<string>();

        foreach (var directory in directories)
        {
            long directoryBytes = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                directoryBytes += new FileInfo(file).Length;
                files++;
            }

            bytes += directoryBytes;
            measured.Add($"{Path.GetFileName(directory)}={directoryBytes}");
        }

        return (bytes, files, string.Join(",", measured));
    }

    /// <summary>
    /// Turns <c>--patterns</c> input into what <c>BuildIndexAsync</c> expects: extensions with a leading
    /// dot, semicolon separated. A caller-supplied leading <c>*</c> is stripped, because the engine
    /// prepends one itself (<c>"*.cs"</c> would otherwise become <c>"**.cs"</c> and match nothing).
    /// </summary>
    public static string? NormalizePatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.StartsWith('*') ? part[1..] : part)
            .Where(part => part.Length > 0)
            .ToArray();

        return parts.Length == 0 ? null : string.Join(';', parts);
    }

    /// <summary>Same normalisation, but as the extension array the corpus builder filters on.</summary>
    public static string[]? NormalizePatternArray(string? raw)
    {
        var normalized = NormalizePatterns(raw);
        return normalized is null
            ? null
            : normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
