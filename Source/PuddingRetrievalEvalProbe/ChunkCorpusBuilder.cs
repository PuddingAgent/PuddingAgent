using System.Diagnostics;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingIndexChunking;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// The chunked corpus of one scope: the documents to index plus the accounting a comparison report
/// needs (files, bytes, tokens before/after filtering, chunks per tier, outline failures).
/// </summary>
internal sealed class ChunkCorpusBuild
{
    public required IReadOnlyList<IndexChunkDocument> Documents { get; init; }

    /// <summary>Chunks per tier label, e.g. <c>{"Outline": 214, "DocComment": 97, "CodeText": 310}</c>.</summary>
    public required IReadOnlyDictionary<string, int> ChunksPerTier { get; init; }

    /// <summary>Tokens in the raw source text of the walked files (the "before filtering" denominator).</summary>
    public required int RawTokens { get; init; }

    /// <summary>Tokens the minimum-length rule removed from the raw text.</summary>
    public required int RemovedAsShort { get; init; }

    /// <summary>Tokens the keyword rule removed from the raw text.</summary>
    public required int RemovedAsStopWord { get; init; }

    /// <summary>Tokens actually present in the indexed documents (after filtering and tier selection).</summary>
    public required int IndexedTokens { get; init; }

    public required int SourceFiles { get; init; }

    public required int SkippedFiles { get; init; }

    public required long SourceBytes { get; init; }

    public required int OutlineSymbolCount { get; init; }

    /// <summary>Outline adapters that reported an error, capped for the log (max 20 entries).</summary>
    public required IReadOnlyList<string> OutlineErrors { get; init; }

    /// <summary>Walk + outline + chunk + filter (excludes the Lucene write).</summary>
    public required long CorpusMs { get; init; }
}

/// <summary>
/// Builds the chunked corpus for the outline-first strategy (U4-1a): walks the scope with <b>the same
/// predicates the engine uses</b> (extension whitelist, excluded directories, size limits), turns each
/// file into chunks through <see cref="FileChunkAssembler"/>, and converts each chunk into an
/// <see cref="IndexChunkDocument"/> whose boost is the chunk's priority.
/// <para>
/// Reusing the engine's own <see cref="FullTextIndexOptions"/> predicates is deliberate: if the two
/// strategies walked different file sets, an index-size comparison between them would be meaningless.
/// </para>
/// <para>The walk is a single pass and the file list is sorted, so two runs produce identical corpora.</para>
/// </summary>
internal static class ChunkCorpusBuilder
{
    public static async Task<ChunkCorpusBuild> BuildAsync(
        string scope,
        FullTextIndexOptions options,
        string[]? patterns,
        ChunkingOptions chunking,
        IOutlineSource outlineSource,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var assembler = new FileChunkAssembler(outlineSource, chunking);
        var documents = new List<IndexChunkDocument>();
        var perTier = new Dictionary<string, int>(StringComparer.Ordinal);
        var outlineErrors = new List<string>();

        long sourceBytes = 0;
        var sourceFiles = 0;
        var skippedFiles = 0;
        var rawTokens = 0;
        var removedAsShort = 0;
        var removedAsStopWord = 0;
        var indexedTokens = 0;
        var outlineSymbols = 0;

        var stopwatch = Stopwatch.StartNew();

        var files = EnumerateFiles(scope, options, patterns).ToArray();
        log($"CHUNK  scope files = {files.Length}");

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                skippedFiles++;
                log($"CHUNK  skip (read failed): {Path.GetRelativePath(scope, file)} — {ex.GetType().Name}");
                continue;
            }

            var language = LanguageOf(file);

            // Rule accounting is measured on the raw file text: that is the denominator a reviewer can
            // reproduce from the file alone. The index holds fewer tokens whenever tiers are dropped, which
            // is why IndexedTokens is reported separately instead of being conflated with "kept".
            var rawReport = assembler.Filter.ApplyDetailed(text, language);
            rawTokens += rawReport.Report.TokensIn;
            removedAsShort += rawReport.Report.RemovedAsShort;
            removedAsStopWord += rawReport.Report.RemovedAsStopWord;

            var assembly = await assembler.AssembleAsync(file, text, language, cancellationToken).ConfigureAwait(false);
            outlineSymbols += assembly.OutlineSymbolCount;
            if (!string.IsNullOrWhiteSpace(assembly.OutlineError) && outlineErrors.Count < 20)
                outlineErrors.Add($"{Path.GetRelativePath(scope, file)}: {assembly.OutlineError}");

            sourceFiles++;
            sourceBytes += new FileInfo(file).Length;

            foreach (var chunk in assembly.Chunks)
            {
                documents.Add(new IndexChunkDocument(
                    chunk.SourceFile,
                    chunk.StartLine,
                    chunk.EndLine,
                    chunk.Text,
                    chunk.Kind.ToString(),
                    ChunkPriorities.BoostOf(chunk.Kind)));

                perTier[chunk.Kind.ToString()] = perTier.GetValueOrDefault(chunk.Kind.ToString()) + 1;
                indexedTokens += ChunkTokenizer.Tokenize(chunk.Text).Count;
            }
        }

        stopwatch.Stop();

        return new ChunkCorpusBuild
        {
            Documents = documents,
            ChunksPerTier = perTier,
            RawTokens = rawTokens,
            RemovedAsShort = removedAsShort,
            RemovedAsStopWord = removedAsStopWord,
            IndexedTokens = indexedTokens,
            SourceFiles = sourceFiles,
            SkippedFiles = skippedFiles,
            SourceBytes = sourceBytes,
            OutlineSymbolCount = outlineSymbols,
            OutlineErrors = outlineErrors,
            CorpusMs = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Single pass over the scope using the engine's predicates. <paramref name="patterns"/> entries are
    /// extensions with a leading dot (e.g. <c>.cs</c>); <c>null</c> means "the engine's default whitelist".
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string scope, FullTextIndexOptions options, string[]? patterns)
    {
        var allowed = patterns is { Length: > 0 }
            ? new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase)
            : null;

        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(scope, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);

            if (allowed is not null)
            {
                if (!allowed.Contains(extension))
                    continue;
            }
            else if (!options.IsIndexableExtension(extension))
            {
                continue;
            }

            if (options.IsExcludedPath(file, scope))
                continue;

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                continue;
            }

            if (length == 0 || length > options.MaxFileSizeBytes)
                continue;

            found.Add(file);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Maps a file extension to the language stratum the filter rules are keyed by.</summary>
    public static SourceLanguage LanguageOf(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".cs" => SourceLanguage.CSharp,
        ".ts" or ".tsx" or ".js" or ".jsx" => SourceLanguage.TypeScript,
        ".py" => SourceLanguage.Python,
        ".md" or ".mdx" => SourceLanguage.Markdown,
        _ => SourceLanguage.PlainText,
    };
}
