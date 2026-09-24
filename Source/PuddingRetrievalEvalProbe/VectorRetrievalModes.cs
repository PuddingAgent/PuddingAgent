using System.Diagnostics;
using System.Globalization;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingIndexChunking;
using PuddingVectorIndex;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Everything the vector / hybrid index mode needs, in one value. Passed as a record instead of twenty
/// positional arguments so a caller cannot silently swap two strings.
/// </summary>
internal sealed record VectorIndexRequest(
    string Retriever,
    string Strategy,
    LuceneSearchEngine Engine,
    FullTextIndexOptions IndexOptions,
    string Scope,
    string RepoRoot,
    string Label,
    string IndexRoot,
    string VectorStoreDirectory,
    string? Patterns,
    string? IndexJsonPath,
    string Tiers,
    string Filter,
    string? ProvidersConfig,
    string? EmbeddingRouteOverride,
    string? EmbeddingBaseUrlOverride,
    int? EmbeddingDimensionsOverride,
    int EmbeddingBatchSize,
    int RrfK,
    double RrfWeightFullText,
    double RrfWeightVector,
    int FusionDepth);

/// <summary>
/// The vector-side composition root: resolves the route, builds the provider, verifies the service is
/// actually reachable, embeds the chunk corpus, writes the store, and (for hybrid) also builds the
/// Lucene chunk index over <b>the same documents</b>.
/// <para>
/// The last point is the whole reason this slice is comparable with U4-1a: both retrievers consume the
/// identical chunk corpus produced by <c>PuddingIndexChunking</c>, so a difference between them is a
/// difference of <i>engine</i>, not of chunking (ADR-089 结构化地图检索设计 §6).
/// </para>
/// </summary>
internal static class VectorRetrievalModes
{
    public static OpenAiCompatibleEmbeddingProvider CreateProvider(ResolvedEmbedding resolved) =>
        new(
            resolved.Route,
            resolved.BaseUrl,
            resolved.ApiKey,
            resolved.Dimensions ?? 0,
            resolved.MaxContextTokens,
            resolved.IsLocal);

    /// <summary>
    /// One real call to prove the service is up, and to learn the dimension it actually produces.
    /// <para>
    /// This is the A6 requirement implemented as code: if the local service is not running, the mode
    /// throws with the endpoint and the reason and the process exits non-zero. It never falls back to
    /// full-text, because a silent fallback would return numbers labelled "vector" that answer a
    /// different question.
    /// </para>
    /// </summary>
    public static async Task<EmbeddingCallStats> VerifyReachableAsync(
        OpenAiCompatibleEmbeddingProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var before = provider.Stats;

        try
        {
            var probe = await provider.EmbedAsync("embedding reachability probe", cancellationToken).ConfigureAwait(false);
            if (probe.Length == 0)
                throw new InvalidOperationException("the service returned an empty vector");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"embedding route '{provider.RouteLabel}' is unreachable at {provider.Endpoint}: "
                + $"{ex.GetType().Name}: {ex.Message}. The vector retriever fails closed: it does NOT "
                + "silently degrade to full-text search (that would answer a different question with the "
                + "same exported numbers).",
                ex);
        }

        if (provider.DeclaredDimensions > 0 && provider.DeclaredDimensions != provider.ObservedDimensions)
            throw new InvalidOperationException(
                $"the configuration declares {provider.DeclaredDimensions} dimensions for route "
                + $"'{provider.RouteLabel}' but {provider.Endpoint} returned {provider.ObservedDimensions}; "
                + "vectors of a different length would not be comparable with the index");

        return provider.Stats - before;
    }

    /// <summary>Builds the vector store (and, for hybrid, the Lucene chunk index) and writes the JSON report.</summary>
    public static async Task<int> RunIndexAsync(VectorIndexRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Strategy, "outline", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"--retriever {request.Retriever} requires --chunks outline: the vector side indexes chunk "
                + "documents, and the 'plain' strategy has none (it is the engine's own per-line walk)");
            return 5;
        }

        var resolved = EmbeddingRouteResolver.Resolve(
            request.ProvidersConfig,
            request.EmbeddingRouteOverride,
            request.EmbeddingBaseUrlOverride,
            request.EmbeddingDimensionsOverride);

        using var provider = CreateProvider(resolved);

        Console.WriteLine("EMBED  --- embedding route resolved from configuration ---");
        Console.WriteLine($"EMBED  route      = {resolved.Route}");
        Console.WriteLine($"EMBED  endpoint   = {provider.Endpoint}");
        Console.WriteLine($"EMBED  config     = {resolved.ConfigPath}");
        Console.WriteLine($"EMBED  isLocal    = {resolved.IsLocal}");
        Console.WriteLine($"EMBED  declaredDim= {(resolved.Dimensions?.ToString(CultureInfo.InvariantCulture) ?? "<unstated: measured by the preflight call>")}");
        Console.WriteLine($"EMBED  maxCtx     = {resolved.MaxContextTokens}");
        Console.WriteLine($"EMBED  price1M    = {(resolved.PricePer1MInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "<unstated>")}");

        var preflight = await VerifyReachableAsync(provider, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"EMBED  preflight  = {preflight}");
        Console.WriteLine($"EMBED  observedDim= {provider.ObservedDimensions}");

        if (!resolved.IsLocal && (resolved.PricePer1MInputTokens is null || resolved.PricePer1MInputTokens > 0))
        {
            Console.WriteLine(
                $"EMBED  WARNING    = route '{resolved.Route}' is NOT local and costs "
                + $"{(resolved.PricePer1MInputTokens?.ToString(CultureInfo.InvariantCulture) ?? "an unstated amount of")} "
                + "per 1M input tokens; this run would incur a real cost that must be listed per call.");
        }

        var chunking = Program.BuildChunkingOptions(request.Tiers, request.Filter);
        var patternArray = IndexProbeFiles.NormalizePatternArray(request.Patterns);

        var total = Stopwatch.StartNew();

        Console.WriteLine($"INDEX  building chunk corpus over {request.Scope} ...");
        var corpus = await ChunkCorpusBuilder.BuildAsync(
            request.Scope,
            request.IndexOptions,
            patternArray,
            chunking,
            new RoslynCSharpOutlineSource(),
            message => Console.WriteLine(message),
            CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine($"CHUNK  sourceFiles  = {corpus.SourceFiles}");
        Console.WriteLine($"CHUNK  sourceBytes  = {corpus.SourceBytes}");
        Console.WriteLine($"CHUNK  skippedFiles = {corpus.SkippedFiles}");
        Console.WriteLine($"CHUNK  symbols      = {corpus.OutlineSymbolCount}");
        Console.WriteLine($"CHUNK  documents    = {corpus.Documents.Count}");
        Console.WriteLine($"CHUNK  perTier      = {string.Join(", ", corpus.ChunksPerTier.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}");
        Console.WriteLine($"CHUNK  corpusMs     = {corpus.CorpusMs}");
        foreach (var outlineError in corpus.OutlineErrors)
            Console.WriteLine($"CHUNK  outlineError = {outlineError}");

        var documents = BuildVectorDocuments(corpus, request.RepoRoot);
        Console.WriteLine($"VEC    documents    = {documents.Count}");

        var build = await VectorIndexBuilder
            .BuildAsync(provider, documents, new VectorIndexBuildOptions(request.EmbeddingBatchSize))
            .ConfigureAwait(false);

        var indexStats = provider.Stats - preflight;
        Console.WriteLine($"VEC    dimensions   = {build.Dimensions}");
        Console.WriteLine($"VEC    indexCalls   = {build.EmbeddingCalls}");
        Console.WriteLine($"VEC    embedMs      = {build.EmbeddingMs}");
        Console.WriteLine($"VEC    statsDelta   = {indexStats}");

        var manifest = new VectorStoreManifest(
            VectorStore.Schema,
            resolved.Route.ToString(),
            build.Dimensions,
            build.Index.Count,
            request.EmbeddingBatchSize,
            build.EmbeddingCalls,
            build.EmbeddingMs,
            resolved.Dimensions,
            build.Index.Entries
                .Select(entry => new VectorStoreEntry(
                    entry.Id,
                    entry.SourceFile,
                    entry.StartLine,
                    entry.EndLine,
                    entry.Kind,
                    entry.Boost))
                .ToArray(),
            DateTimeOffset.UtcNow.ToString("O"),
            "Provenance only; the chunk text is not stored (see VectorIndexEntry). Rows are raw little-endian "
            + "float32, in entry order, un-normalised and un-quantised.");

        var storeWrite = Stopwatch.StartNew();
        var (storeBytes, vectorBytes, manifestBytes) = VectorStore.Write(
            request.VectorStoreDirectory, build.Index, manifest);
        storeWrite.Stop();

        Console.WriteLine($"VSTORE directory  = {request.VectorStoreDirectory}");
        Console.WriteLine($"VSTORE bytes      = {storeBytes} (vectors={vectorBytes} manifest={manifestBytes})");

        long? luceneWriteMs = null;
        int? luceneDocuments = null;
        long? engineMs = null;
        long? luceneTextChars = null;
        var luceneSuccess = true;
        string? luceneError = null;

        if (string.Equals(request.Retriever, "hybrid", StringComparison.Ordinal))
        {
            Console.WriteLine($"INDEX  writing Lucene chunk index over the same {corpus.Documents.Count} documents ...");
            var write = Stopwatch.StartNew();
            var result = await request.Engine
                .BuildChunkIndexAsync(request.Scope, corpus.Documents, CancellationToken.None)
                .ConfigureAwait(false);
            write.Stop();

            luceneWriteMs = write.ElapsedMilliseconds;
            luceneDocuments = result.DocumentCount;
            engineMs = result.ElapsedMs;
            luceneTextChars = result.TotalTextChars;
            luceneSuccess = result.Success;
            luceneError = result.Error;

            Console.WriteLine($"INDEX  luceneDocs = {result.DocumentCount}");
            Console.WriteLine($"INDEX  textChars  = {result.TotalTextChars}");
            Console.WriteLine($"INDEX  engineMs   = {result.ElapsedMs}");
            Console.WriteLine($"INDEX  writeMs    = {write.ElapsedMilliseconds}");
            Console.WriteLine($"INDEX  success    = {result.Success}");
            Console.WriteLine($"INDEX  error      = {result.Error ?? "<none>"}");
        }

        total.Stop();

        var (indexBytes, indexFiles, indexDirectory) = IndexProbeFiles.MeasureIndexDirectory(request.IndexRoot);
        Console.WriteLine($"INDEX  indexBytes = {indexBytes} ({indexBytes / 1024.0 / 1024.0:0.###} MB)");
        Console.WriteLine($"INDEX  indexFiles = {indexFiles}");
        Console.WriteLine($"INDEX  indexDir   = {indexDirectory}");
        Console.WriteLine($"INDEX  harnessMs  = {total.ElapsedMilliseconds}");
        Console.WriteLine($"INDEX  hasIndex   = {request.Engine.HasIndex(request.Scope)}");

        if (request.IndexJsonPath is not null)
        {
            VectorIndexRunReport.Write(request.IndexJsonPath, new VectorIndexRunReport
            {
                Retriever = request.Retriever,
                Strategy = request.Strategy,
                Tiers = request.Tiers,
                Filter = request.Filter,
                Scope = request.Scope,
                ScopeLabel = request.Label,
                IndexRoot = Path.GetFullPath(request.IndexRoot),
                IndexDirectory = indexDirectory,
                IndexBytes = indexBytes,
                IndexFileCount = indexFiles,
                SourceFiles = corpus.SourceFiles,
                LuceneDocuments = luceneDocuments,
                VectorDocuments = build.Index.Count,
                IndexedTextChars = luceneTextChars,
                RawTokens = corpus.RawTokens,
                RemovedAsShort = corpus.RemovedAsShort,
                RemovedAsStopWord = corpus.RemovedAsStopWord,
                IndexedTokens = corpus.IndexedTokens,
                ChunksPerTier = corpus.ChunksPerTier.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                CorpusMs = corpus.CorpusMs,
                LuceneWriteMs = luceneWriteMs,
                EngineMs = engineMs,
                EmbeddingRoute = resolved.Route.ToString(),
                EmbeddingEndpoint = provider.Endpoint,
                EmbeddingDeclaredDimensions = resolved.Dimensions,
                EmbeddingObservedDimensions = provider.ObservedDimensions,
                EmbeddingIsLocal = resolved.IsLocal,
                EmbeddingMaxContextTokens = resolved.MaxContextTokens,
                EmbeddingPricePer1MInputTokens = resolved.PricePer1MInputTokens,
                ProvidersConfigPath = resolved.ConfigPath,
                EmbeddingBatchSize = request.EmbeddingBatchSize,
                IndexEmbeddingCalls = build.EmbeddingCalls,
                IndexEmbeddingMs = build.EmbeddingMs,
                IndexEmbeddedTexts = indexStats.Texts,
                PreflightCalls = preflight.Calls,
                PreflightMs = preflight.ElapsedMs,
                IndexPromptTokens = indexStats.PromptTokens,
                VectorStoreBytes = storeBytes,
                VectorVectorsBytes = vectorBytes,
                VectorManifestBytes = manifestBytes,
                VectorStoreWriteMs = storeWrite.ElapsedMilliseconds,
                RrfK = string.Equals(request.Retriever, "hybrid", StringComparison.Ordinal) ? request.RrfK : null,
                RrfWeightFullText = string.Equals(request.Retriever, "hybrid", StringComparison.Ordinal) ? request.RrfWeightFullText : null,
                RrfWeightVector = string.Equals(request.Retriever, "hybrid", StringComparison.Ordinal) ? request.RrfWeightVector : null,
                FusionDepth = string.Equals(request.Retriever, "hybrid", StringComparison.Ordinal) ? request.FusionDepth : null,
                BuildMs = total.ElapsedMilliseconds,
                Success = luceneSuccess,
                Error = luceneError,
                RecordedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Note = "embedMs/IndexEmbeddingCalls are the vector service only; corpusMs is walk+outline+chunk+filter "
                     + "and is common to every retriever; the preflight call is reported separately so it is not "
                     + "mistaken for indexing work. VectorStoreBytes is vectors.f32 + manifest.json, un-quantised.",
            });
        }

        return luceneSuccess ? 0 : 3;
    }

    /// <summary>
    /// Maps the chunk corpus onto vector documents, using <b>repository-relative</b> slash-separated paths.
    /// The path is what the evaluation matches on, and the annotated set stores repository-relative paths,
    /// so keeping them relative here is what makes "hit" mean the same thing on both retrievers.
    /// </summary>
    private static IReadOnlyList<VectorDocument> BuildVectorDocuments(ChunkCorpusBuild corpus, string repoRoot)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<VectorDocument>(corpus.Documents.Count);

        foreach (var chunk in corpus.Documents)
        {
            var relative = Path.GetRelativePath(repoRoot, chunk.Path).Replace('\\', '/');
            var baseId = $"{relative}:{chunk.StartLine}-{chunk.EndLine}";
            var id = baseId;
            var suffix = 1;

            while (!usedIds.Add(id))
            {
                suffix++;
                id = baseId + "#" + suffix.ToString(CultureInfo.InvariantCulture);
            }

            documents.Add(new VectorDocument(
                id,
                relative,
                chunk.StartLine,
                chunk.EndLine,
                chunk.Kind,
                chunk.Boost,
                chunk.Text));
        }

        return documents;
    }
}
