using System.Diagnostics;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// U4-0 baseline harness. Three read-only modes:
/// <list type="bullet">
/// <item><description><c>count</c> — corpus inventory (see <see cref="CorpusInventory"/>).</description></item>
/// <item><description><c>index</c> — build/refresh the Lucene index for the scope (timed).</description></item>
/// <item><description><c>measure</c> — run the annotated query set through <c>ISearchProbe</c> and write the Markdown + JSON report.</description></item>
/// </list>
/// It never mutates repository sources, never touches DI/Host, and writes only under temp/ and the
/// report path it is given.
/// <para>
/// <b>Scope discipline.</b> A probe call is confined to one root directory. <c>--expected-under &lt;prefix&gt;</c>
/// keeps only the annotated cases whose expected hits <b>all</b> live under that prefix, so a scoped run
/// never scores a case whose answer is structurally out of scope (which would be a fake zero, not a
/// measurement). The number of dropped cases is always printed.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PROBE FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var options = ProbeOptions.Parse(args);

        var repoRoot = options.Get("repo-root") ?? FindRepoRoot();
        var scope = options.Get("scope") ?? repoRoot;
        var indexRoot = options.Get("index-root") ?? Path.Combine(repoRoot, "temp", "U4-0-probe", "index");
        var setPath = options.Get("set") ?? Path.Combine(repoRoot, "Source", "PuddingRetrievalEval", "eval", "sets", "seed-v1.json");
        var outBase = options.Get("out")
                      ?? Path.Combine(repoRoot, "Source", "PuddingRetrievalEval", "eval", "reports", "baseline-" + DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var label = options.Get("label") ?? Path.GetFileName(scope);
        var mode = (options.Get("mode") ?? "count").ToLowerInvariant();
        var expectedUnder = options.Get("expected-under");
        var extensions = options.Get("ext") is { Length: > 0 } ext
            ? ext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
        var perLanguage = options.Get("per-language") is { Length: > 0 } languageList
            ? languageList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        Console.WriteLine($"mode          = {mode}");
        Console.WriteLine($"repoRoot      = {repoRoot}");
        Console.WriteLine($"scope         = {scope}");
        Console.WriteLine($"indexRoot     = {indexRoot}");
        Console.WriteLine($"set           = {setPath}");
        Console.WriteLine($"outBase       = {outBase}");
        Console.WriteLine($"label         = {label}");
        Console.WriteLine($"expectedUnder = {expectedUnder ?? "<none>"}");
        Console.WriteLine($"extensions    = {(extensions is null ? "<none>" : string.Join(",", extensions))}");
        Console.WriteLine();

        var indexOptions = new FullTextIndexOptions { IndexRootDirectory = indexRoot };
        var engine = new LuceneSearchEngine(indexOptions);

        switch (mode)
        {
            case "count":
                return CorpusInventory.Run(scope, indexOptions, options.GetInt("top", 25));

            case "index":
            {
                Console.WriteLine($"INDEX  building over {scope} ...");
                var stopwatch = Stopwatch.StartNew();
                var result = await engine.BuildIndexAsync(scope, null, CancellationToken.None).ConfigureAwait(false);
                stopwatch.Stop();
                Console.WriteLine($"INDEX  success      = {result.Success}");
                Console.WriteLine($"INDEX  filesIndexed = {result.IndexedFileCount}");
                Console.WriteLine($"INDEX  totalBytes   = {result.TotalBytes}");
                Console.WriteLine($"INDEX  engineMs     = {result.ElapsedMs}");
                Console.WriteLine($"INDEX  harnessMs    = {stopwatch.ElapsedMilliseconds}");
                Console.WriteLine($"INDEX  error        = {result.Error ?? "<none>"}");
                Console.WriteLine($"INDEX  hasIndex     = {engine.HasIndex(scope)}");
                return result.Success ? 0 : 3;
            }

            case "measure":
            {
                if (!engine.HasIndex(scope))
                {
                    Console.Error.WriteLine($"scope '{scope}' has no index yet — run --mode index first");
                    return 2;
                }

                var loaded = EvalSetLoader.LoadFromFile(setPath);
                var set = RestrictCases(loaded, expectedUnder);

                var probe = new LuceneFullTextProbe(engine);
                var runOptions = new EvalRunOptions(
                    new SearchScope(scope, extensions),
                    label,
                    options.GetInt("max-results", 20),
                    options.GetInt("warmup", 1),
                    options.GetInt("measured", 3));

                var run = await new EvalRunner(probe)
                    .RunAsync(set, runOptions, CancellationToken.None)
                    .ConfigureAwait(false);

                var reproduce = "dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- "
                                + $"--mode measure --scope \"{scope}\" --index-root \"{indexRoot}\" --set \"{setPath}\" "
                                + $"--out \"{outBase}\" --label {label} --warmup {runOptions.WarmupRepetitions} "
                                + $"--measured {runOptions.MeasuredRepetitions} --max-results {runOptions.MaxResults}"
                                + (expectedUnder is null ? string.Empty : $" --expected-under {expectedUnder}");

                EvalReportWriter.Write(outBase, run, reproduce);

                Console.WriteLine($"RUN   probe={run.ProbeName} set={run.SetName} v{run.SetVersion} cases={run.CaseCount} failed={run.FailedCaseCount}");
                Console.WriteLine($"RUN   recall@1={run.RecallAt1:0.0000} recall@5={run.RecallAt5:0.0000} recall@10={run.RecallAt10:0.0000} MRR={run.Mrr:0.0000}");
                Console.WriteLine($"RUN   precision@5={run.PrecisionAt5:0.0000} precision@10={run.PrecisionAt10:0.0000} noiseRate@10={run.NoiseRateAt10:0.0000}");
                Console.WriteLine($"RUN   cold(n={run.ColdLatency.Count}) p50={run.ColdLatency.P50Ms:0.###} p95={run.ColdLatency.P95Ms:0.###} p99={run.ColdLatency.P99Ms:0.###} max={run.ColdLatency.MaxMs:0.###} mean={run.ColdLatency.MeanMs:0.###}");
                Console.WriteLine($"RUN   warm(n={run.WarmLatency.Count}) p50={run.WarmLatency.P50Ms:0.###} p95={run.WarmLatency.P95Ms:0.###} p99={run.WarmLatency.P99Ms:0.###} max={run.WarmLatency.MaxMs:0.###} mean={run.WarmLatency.MeanMs:0.###}");
                Console.WriteLine($"RUN   totalMs={run.TotalElapsedMs} repetitionStable={run.AllRepetitionsIdentical}");
                foreach (var slice in run.Slices)
                    Console.WriteLine($"RUN   [{slice.Language}] n={slice.CaseCount} recall@1={slice.RecallAt1:0.0000} recall@5={slice.RecallAt5:0.0000} recall@10={slice.RecallAt10:0.0000} MRR={slice.Mrr:0.0000} noise@10={slice.NoiseRateAt10:0.0000}");
                Console.WriteLine($"RUN   wrote {outBase}.md and {outBase}.json");

                if (perLanguage is not null)
                    await WritePerLanguageReportsAsync(scope, set, label, outBase, engine, options, perLanguage).ConfigureAwait(false);

                return run.FailedCaseCount == 0 ? 0 : 4;
            }

            default:
                Console.Error.WriteLine($"unknown mode '{mode}' (expected count|index|measure)");
                return 5;
        }
    }

    /// <summary>
    /// Keeps only the cases whose every expected hit lives under <paramref name="expectedUnder"/>.
    /// Returns the set unchanged when no prefix is given.
    /// </summary>
    private static EvalSet RestrictCases(EvalSet set, string? expectedUnder)
    {
        if (string.IsNullOrWhiteSpace(expectedUnder))
            return set;

        var prefix = PathIdentity.Normalize(expectedUnder).TrimEnd('/') + "/";

        var kept = set.Cases
            .Where(c => c.ExpectedHits.All(hit =>
            {
                var (path, _) = PathIdentity.SplitExpected(hit);
                return (PathIdentity.Normalize(path) + "/").StartsWith(prefix, StringComparison.Ordinal);
            }))
            .ToArray();

        Console.WriteLine($"SET   total={set.Cases.Count} keptUnder='{expectedUnder}'={kept.Length} dropped={set.Cases.Count - kept.Length}");

        foreach (var dropped in set.Cases.Except(kept))
            Console.WriteLine($"SET   dropped: [{dropped.Kind}/{dropped.Language}] {dropped.Query}");

        Console.WriteLine();

        return new EvalSet(set.Name, set.Version, kept);
    }

    /// <summary>
    /// Splits the run per language stratum by restricting the scope's extension filter, so a
    /// "per-language latency" claim is backed by numbers measured separately instead of one blended
    /// average. Reports are written next to the main one.
    /// </summary>
    private static async Task WritePerLanguageReportsAsync(
        string scope, EvalSet set, string label, string outBase,
        LuceneSearchEngine engine, ProbeOptions options, string[] languages)
    {
        var extensionsByLanguage = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = [".cs"],
            ["typescript"] = [".ts", ".tsx"],
            ["markdown"] = [".md"],
        };

        foreach (var language in languages)
        {
            if (!extensionsByLanguage.TryGetValue(language, out var extensions))
            {
                Console.WriteLine($"RUN   [skip] no extension mapping for language '{language}'");
                continue;
            }

            var filtered = new EvalSet(
                set.Name,
                set.Version,
                set.Cases.Where(c => string.Equals(c.Language.ToString(), language, StringComparison.OrdinalIgnoreCase)).ToArray());

            if (filtered.Cases.Count == 0)
            {
                Console.WriteLine($"RUN   [skip] set has no cases for language '{language}'");
                continue;
            }

            var probe = new LuceneFullTextProbe(engine);
            var runOptions = new EvalRunOptions(
                new SearchScope(scope, extensions),
                $"{label}:{language}",
                options.GetInt("max-results", 20),
                options.GetInt("warmup", 1),
                options.GetInt("measured", 3));

            var run = await new EvalRunner(probe).RunAsync(filtered, runOptions, CancellationToken.None).ConfigureAwait(false);
            var basePath = $"{outBase}.{language.ToLowerInvariant()}";

            EvalReportWriter.Write(basePath, run, null);
            Console.WriteLine($"RUN   [{language}] cases={run.CaseCount} recall@1={run.RecallAt1:0.0000} recall@5={run.RecallAt5:0.0000} recall@10={run.RecallAt10:0.0000} MRR={run.Mrr:0.0000} warmP50={run.WarmLatency.P50Ms:0.###} warmP95={run.WarmLatency.P95Ms:0.###} -> {basePath}.md");
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "could not locate the repository root (PuddingAgentNetwork.slnx) above " + AppContext.BaseDirectory);
    }
}

/// <summary>Minimal <c>--key value</c> / <c>--flag</c> parser.</summary>
internal sealed class ProbeOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static ProbeOptions Parse(string[] args)
    {
        var options = new ProbeOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"unexpected argument '{token}'");

            var key = token[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[key] = args[++i];
            }
            else
            {
                options._values[key] = "true";
            }
        }

        return options;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
}
