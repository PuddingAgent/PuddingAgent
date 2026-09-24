using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PuddingRetrievalEval.Services;
using PuddingVectorIndex;

namespace PuddingRetrievalEvalProbe;

/// <summary>One ranked window, reduced to what a comparison needs: the ids in rank order plus their scores.</summary>
internal sealed record ScanRun(string[] Ids, double[] Scores);

/// <summary>One path's raw samples: per-query milliseconds, the windows it produced, and what a call allocated.</summary>
internal sealed record TimingRun(double[] Ms, ScanRun[] Runs, double AllocatedBytesPerQuery);

/// <summary>The numbers one path produced at one corpus size.</summary>
internal sealed record ScanPathReport(
    string Path,
    string Description,
    int Samples,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MinMs,
    double MaxMs,
    double MeanMs,
    double RowsPerSecond,
    double AllocatedBytesPerQuery,
    int RankingMismatchesVsShipped,
    double MaxScoreDeltaVsShipped,
    bool BitIdenticalToShipped);

/// <summary>One point of the size/latency curve: the corpus, its cost to build, and every path measured on it.</summary>
internal sealed record ScanPointReport(
    int Rows,
    int RealRows,
    double BuildMs,
    long CodesBytes,
    long Float32Bytes,
    IReadOnlyList<ScanPathReport> Paths);

/// <summary>The R5 allocation comparison: the two numbers and their ratio, measured in one process.</summary>
internal sealed record ScanAllocationReport(
    int Rows,
    int TopK,
    int Repetitions,
    double ShippedBytesPerQuery,
    double InPlaceBytesPerQuery,
    double InPlaceShareOfShipped);

/// <summary>The whole run, written next to the console table so a report can quote one file.</summary>
internal sealed record ScanBenchReport(
    string Schema,
    string PoolStore,
    string PoolRoute,
    int PoolRows,
    int Dimensions,
    int TopK,
    int QueryCount,
    int WarmupRounds,
    int Seed,
    int NoisePercent,
    int ProcessorCount,
    string MachineName,
    string Framework,
    string GcMode,
    IReadOnlyList<int> Points,
    IReadOnlyList<ScanPointReport> Results,
    ScanAllocationReport Allocation,
    string RecordedAtUtc);

/// <summary>
/// The U4-3c size/latency + equivalence + allocation probe for the in-place int8 scan.
/// <para>
/// <b>What is measured.</b> Four paths over the same rows, so a difference between them is a difference
/// of scan organisation and never of what is compared:
/// </para>
/// <list type="number">
/// <item><description><c>shipped</c> — what a loaded int8 store does today: <c>ToVectorEntry()</c> per row
/// (dequantised into 4,096 float32 bytes in RAM at load) into <c>InMemoryVectorIndex</c>, whose
/// <c>Search</c> materialises one result per row and sorts all of them.</description></item>
/// <item><description><c>inplace-component</c> — the delivered
/// <see cref="QuantizedInMemoryVectorIndex"/>: codes scanned where they lie, row norms precomputed once,
/// bounded top-k.</description></item>
/// <item><description><c>diag-insert-precomputed</c> — a probe-local control with the same arithmetic, the
/// same precomputed norms and the same bounded selection, but bounded <i>insertion</i> instead of a heap.
/// It reproduces the U4-3b harness' <c>int8-inplace</c> shape, which is how the instrument itself is
/// cross-checked against that report.</description></item>
/// <item><description><c>diag-insert-inline-norm</c> — the same scanner with the row norm recomputed
/// inside the scan. It isolates the cost of the one design decision behind
/// <c>inplace-component</c> (precompute the norm once per row instead of once per query per row), so the
/// decision is backed by a measurement rather than by an assertion.</description></item>
/// </list>
/// <para>
/// <b>Rows are a prefix of one another.</b> Row <c>i</c> is always the same vector whatever the requested
/// point size: rows below the real store's size are that store's own rows (codes byte-identical to what
/// was written), rows above it are perturbations of real rows generated in index order from one seeded
/// RNG. A 65,536-row point is therefore a strict superset of the 10,199-row point, which is what makes the
/// curve's slope meaningful; the real-only point (the store's own row count) is measured with no
/// perturbation at all.
/// </para>
/// <para>
/// Read-only with respect to repository sources and to the store: it loads bytes and writes one JSON file
/// under the path it is given. No embedding service is contacted.
/// </para>
/// </summary>
internal static class VectorScanBench
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(ProbeOptions options, string repoRoot)
    {
        var topK = options.GetInt("topk", 20);
        var queryCount = options.GetInt("queries", 100);
        var warmup = options.GetInt("warmup", 2);
        var seed = options.GetInt("seed", 20260924);
        var noisePercent = options.GetInt("noise-percent", 5);
        var allocationRows = options.GetInt("alloc-rows", 4096);
        var allocationRepetitions = options.GetInt("alloc-repetitions", 50);
        var outPath = options.Get("out");
        var storeDirectory = options.Get("i8-pool")
            ?? Path.Combine(repoRoot, "temp", "U4-3-frozen", "index", "i8-p0", "vector-store");

        var points = (options.Get("points") ?? "475,4096,10199,20000,65536")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();

        if (points.Length == 0 || points.Any(rows => rows <= 0))
            throw new ArgumentException("--points must list at least one positive row count");

        if (!Directory.Exists(storeDirectory))
            throw new DirectoryNotFoundException(
                $"no int8 store at {storeDirectory}; build one with --mode index --retriever vector "
                + "--vector-quantization int8 (or pass --i8-pool <store directory>)");

        var (poolEntries, manifest) = VectorStore.ReadInt8QuantizedEntries(storeDirectory);
        var dimensions = manifest.Dimensions;

        if (options.GetIntOrNull("dim") is { } requested && requested != dimensions)
            throw new ArgumentException(
                $"--dim {requested} does not match the store's declared {dimensions}; the codes are "
                + "already written at one width and must not be re-interpreted");

        var poolFloats = poolEntries.Select(entry => entry.Vector.Dequantize()).ToArray();
        var queries = BuildQueries(poolFloats, queryCount, dimensions, noisePercent, seed + 7919);

        Console.WriteLine("SCAN   schema         = u4-3c-scan-bench-v1");
        Console.WriteLine($"SCAN   poolStore      = {Path.GetFullPath(storeDirectory)}");
        Console.WriteLine($"SCAN   poolRows       = {poolEntries.Count}");
        Console.WriteLine($"SCAN   poolRoute      = {manifest.Route}");
        Console.WriteLine($"SCAN   poolFormat     = {manifest.Format} (codes {dimensions} B + scale 4 B per row)");
        Console.WriteLine($"SCAN   dimensions     = {dimensions}");
        Console.WriteLine($"SCAN   topK           = {topK}");
        Console.WriteLine($"SCAN   queries        = {queryCount}");
        Console.WriteLine($"SCAN   warmupRounds   = {warmup}");
        Console.WriteLine($"SCAN   noisePercent   = {noisePercent}");
        Console.WriteLine($"SCAN   seed           = {seed}");
        Console.WriteLine($"SCAN   points         = {string.Join(", ", points)}");
        Console.WriteLine($"SCAN   processorCount = {Environment.ProcessorCount}");
        Console.WriteLine($"SCAN   gc             = {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}/{System.Runtime.GCSettings.LatencyMode}");
        Console.WriteLine();

        var pointReports = new List<ScanPointReport>();
        foreach (var rows in points)
        {
            pointReports.Add(MeasurePoint(rows, poolEntries, poolFloats, queries, dimensions, topK, warmup, noisePercent, seed));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        var allocation = MeasureAllocation(poolEntries, poolFloats, dimensions, allocationRows, topK, allocationRepetitions, seed);
        PrintTable(pointReports, allocation);

        if (outPath is not null)
        {
            var report = new ScanBenchReport(
                "u4-3c-scan-bench-v1",
                Path.GetFullPath(storeDirectory),
                manifest.Route,
                poolEntries.Count,
                dimensions,
                topK,
                queryCount,
                warmup,
                seed,
                noisePercent,
                Environment.ProcessorCount,
                Environment.MachineName,
                Environment.Version.ToString(),
                (System.Runtime.GCSettings.IsServerGC ? "server" : "workstation") + "/" + System.Runtime.GCSettings.LatencyMode,
                points,
                pointReports,
                allocation,
                DateTimeOffset.UtcNow.ToString("O"));

            var full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
            Console.WriteLine($"SCAN   wrote          = {full}");
        }

        return 0;
    }

    /// <summary>Measures every path at one corpus size and compares each of them with <c>shipped</c>.</summary>
    private static ScanPointReport MeasurePoint(
        int rows,
        IReadOnlyList<QuantizedVectorEntry> poolEntries,
        IReadOnlyList<float[]> poolFloats,
        IReadOnlyList<float[]> queries,
        int dimensions,
        int topK,
        int warmup,
        int noisePercent,
        int seed)
    {
        Console.WriteLine($"SCAN   -- point rows={rows} ----------------------------------------");

        var buildStart = Stopwatch.GetTimestamp();

        var entries = new QuantizedVectorEntry[rows];
        var norms = new double[rows];
        var shipped = new InMemoryVectorIndex(dimensions, EmbeddingRoute.Parse("u4-3c/local-control"));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rng = new Random(seed);
        for (var i = 0; i < rows; i++)
        {
            QuantizedVector vector;
            string id;
            string sourceFile;
            int startLine;
            int endLine;
            string kind;

            if (i < poolEntries.Count)
            {
                var real = poolEntries[i];
                vector = real.Vector;
                id = real.Id;
                sourceFile = real.SourceFile;
                startLine = real.StartLine;
                endLine = real.EndLine;
                kind = real.Kind;
            }
            else
            {
                var source = poolFloats[i % poolFloats.Count];
                var sigma = MaxAbs(source) * noisePercent / 100d;
                var buffer = new float[dimensions];
                for (var d = 0; d < dimensions; d++)
                    buffer[d] = (float)(source[d] + (sigma * NextGaussian(rng)));

                vector = VectorQuantizer.Quantize(buffer);
                id = $"row-{i:D6}";
                sourceFile = "Source/Synthetic/Row.cs";
                startLine = 1;
                endLine = 2;
                kind = "Outline";
            }

            if (!ids.Add(id))
                throw new InvalidOperationException(
                    $"the row set at {rows} rows contains the id '{id}' twice; the run would compare two "
                    + "different corpora without saying so");

            entries[i] = new QuantizedVectorEntry(id, sourceFile, startLine, endLine, kind, 2f, vector);
            norms[i] = NormOf(vector);
            shipped.Add(entries[i].ToVectorEntry());
        }

        var buildMs = Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds;
        var inPlace = new QuantizedInMemoryVectorIndex(dimensions, EmbeddingRoute.Parse("u4-3c/local-control"));
        inPlace.AddRange(entries);

        var codesBytes = (long)rows * (dimensions + sizeof(float));
        var floatBytes = (long)rows * dimensions * sizeof(float);
        Console.WriteLine($"SCAN   build          = {buildMs:0.0} ms (codes={codesBytes / 1024d / 1024d:0.0} MB, float32 rows={floatBytes / 1024d / 1024d:0.0} MB)");

        var shippedTiming = Time(q => Harvest(shipped.Search(queries[q], topK)), queries.Count, warmup);
        var inPlaceTiming = Time(q => Harvest(inPlace.Search(queries[q], topK)), queries.Count, warmup);
        var diagPrecomputed = Time(
            q => Diagnostic(entries, norms, queries[q], topK),
            queries.Count,
            warmup);
        var diagInlineNorm = Time(
            q => Diagnostic(entries, null, queries[q], topK),
            queries.Count,
            warmup);

        var paths = new List<ScanPathReport>
        {
            ToReport(
                "shipped",
                "InMemoryVectorIndex over dequantised float32 rows (what a loaded int8 store does today)",
                shippedTiming,
                rows,
                reference: null),
            ToReport(
                "inplace-component",
                "QuantizedInMemoryVectorIndex: codes scanned in place, norms precomputed, bounded top-k heap",
                inPlaceTiming,
                rows,
                shippedTiming),
            ToReport(
                "diag-insert-precomputed",
                "diagnostic: same arithmetic and precomputed norms, bounded insertion instead of a heap",
                diagPrecomputed,
                rows,
                shippedTiming),
            ToReport(
                "diag-insert-inline-norm",
                "diagnostic: bounded insertion with the row norm recomputed inside the scan",
                diagInlineNorm,
                rows,
                shippedTiming),
        };

        foreach (var path in paths)
        {
            Console.WriteLine(
                $"SCAN   {path.Path,-24} n={path.Samples,3} p50={path.P50Ms,9:0.000} p95={path.P95Ms,9:0.000} "
                + $"p99={path.P99Ms,9:0.000} min={path.MinMs,8:0.000} max={path.MaxMs,8:0.000} "
                + $"rowsPerSec={path.RowsPerSecond,12:0} allocPerQuery={path.AllocatedBytesPerQuery,10:0} "
                + $"mismatchVsShipped={path.RankingMismatchesVsShipped} maxScoreDelta={path.MaxScoreDeltaVsShipped:R} "
                + $"bitIdentical={path.BitIdenticalToShipped}");
        }

        Console.WriteLine(
            $"SCAN   shipped over inplace p95 ratio = "
            + $"{paths[0].P95Ms / Math.Max(paths[1].P95Ms, 1e-9):0.000}");

        return new ScanPointReport(rows, Math.Min(rows, poolEntries.Count), buildMs, codesBytes, floatBytes, paths);
    }

    /// <summary>
    /// R5: the two allocation numbers, measured around the calls only (harvesting the results would add the
    /// harness' own arrays to both paths and blur the comparison).
    /// </summary>
    private static ScanAllocationReport MeasureAllocation(
        IReadOnlyList<QuantizedVectorEntry> poolEntries,
        IReadOnlyList<float[]> poolFloats,
        int dimensions,
        int rows,
        int topK,
        int repetitions,
        int seed)
    {
        Console.WriteLine();
        Console.WriteLine($"SCAN   -- allocation comparison rows={rows} topK={topK} repetitions={repetitions} ---");

        var inPlace = new QuantizedInMemoryVectorIndex(dimensions);
        var shipped = new InMemoryVectorIndex(dimensions);
        var rng = new Random(seed + 104729);
        for (var i = 0; i < rows; i++)
        {
            var source = poolFloats[i % poolFloats.Count];
            var sigma = MaxAbs(source) / 2d;
            var buffer = new float[dimensions];
            for (var d = 0; d < dimensions; d++)
                buffer[d] = (float)(source[d] + (sigma * NextGaussian(rng)));

            var entry = new QuantizedVectorEntry(
                $"alloc-{i:D6}", "Source/Synthetic/Row.cs", 1, 2, "Outline", 2f, VectorQuantizer.Quantize(buffer));
            inPlace.Add(entry);
            shipped.Add(entry.ToVectorEntry());
        }

        var query = BuildQueries(poolFloats, 1, dimensions, 5, seed + 31337)[0];

        for (var warmup = 0; warmup < 3; warmup++)
        {
            GC.KeepAlive(shipped.Search(query, topK));
            GC.KeepAlive(inPlace.Search(query, topK));
        }

        var shippedBytes = AllocatedPerQuery(() =>
        {
            for (var i = 0; i < repetitions; i++)
                GC.KeepAlive(shipped.Search(query, topK));
        }, repetitions);

        var inPlaceBytes = AllocatedPerQuery(() =>
        {
            for (var i = 0; i < repetitions; i++)
                GC.KeepAlive(inPlace.Search(query, topK));
        }, repetitions);

        Console.WriteLine($"SCAN   allocated per query: shipped={shippedBytes:0} B, inplace={inPlaceBytes:0} B "
            + $"(in-place is {inPlaceBytes / Math.Max(shippedBytes, 1d) * 100d:0.00}% of shipped)");

        return new ScanAllocationReport(
            rows,
            topK,
            repetitions,
            shippedBytes,
            inPlaceBytes,
            inPlaceBytes / Math.Max(shippedBytes, 1d));
    }

    private static double AllocatedPerQuery(Action body, int repetitions)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        body();
        var after = GC.GetTotalAllocatedBytes(precise: true);
        return (after - before) / (double)repetitions;
    }

    /// <summary>
    /// The probe-local control scanner: bounded insertion over the codes, with the row norm either taken
    /// from the precomputed array or recomputed inside the scan. It is <b>not</b> a candidate
    /// implementation — it exists to separate "where the norm comes from" from the delivered path's total.
    /// <para>
    /// The two variants are separate methods on purpose: a shared loop with an <c>if (norms is null)</c>
    /// inside it would add one multiply-and-add per component to <i>both</i> variants, and the comparison
    /// between them would then be measuring that extra work rather than the norm's source.
    /// </para>
    /// </summary>
    private static ScanRun Diagnostic(
        IReadOnlyList<QuantizedVectorEntry> entries,
        double[]? norms,
        float[] query,
        int topK) =>
        norms is null
            ? DiagnosticInlineNorm(entries, query, topK)
            : DiagnosticPrecomputed(entries, norms, query, topK);

    private static ScanRun DiagnosticPrecomputed(
        IReadOnlyList<QuantizedVectorEntry> entries,
        double[] norms,
        float[] query,
        int topK)
    {
        var queryNorm = VectorMath.Norm(query);
        var capacity = Math.Min(topK, entries.Count);
        var scores = new double[capacity];
        var rows = new int[capacity];
        var count = 0;

        for (var row = 0; row < entries.Count; row++)
        {
            var codes = entries[row].Vector.Codes.Span;
            var scale = entries[row].Vector.Scale;

            double dot = 0d;
            for (var d = 0; d < codes.Length; d++)
                dot += (double)(codes[d] * scale) * query[d];

            Offer(entries, scores, rows, ref count, capacity, row, dot / (norms[row] * queryNorm));
        }

        return Materialise(entries, scores, rows, count);
    }

    private static ScanRun DiagnosticInlineNorm(
        IReadOnlyList<QuantizedVectorEntry> entries,
        float[] query,
        int topK)
    {
        var queryNorm = VectorMath.Norm(query);
        var capacity = Math.Min(topK, entries.Count);
        var scores = new double[capacity];
        var rows = new int[capacity];
        var count = 0;

        for (var row = 0; row < entries.Count; row++)
        {
            var codes = entries[row].Vector.Codes.Span;
            var scale = entries[row].Vector.Scale;

            double dot = 0d;
            double sumSquares = 0d;
            for (var d = 0; d < codes.Length; d++)
            {
                var component = codes[d] * scale;
                dot += (double)component * query[d];
                sumSquares += (double)component * component;
            }

            Offer(entries, scores, rows, ref count, capacity, row, dot / (Math.Sqrt(sumSquares) * queryNorm));
        }

        return Materialise(entries, scores, rows, count);
    }

    /// <summary>Inserts a candidate into a best-first window of <paramref name="capacity"/> entries.</summary>
    private static void Offer(
        IReadOnlyList<QuantizedVectorEntry> entries,
        double[] scores,
        int[] rows,
        ref int count,
        int capacity,
        int row,
        double score)
    {
        if (count == capacity && Compare(score, entries[row].Id, scores[capacity - 1], entries[rows[capacity - 1]].Id) >= 0)
            return;

        var at = count < capacity ? count : capacity - 1;
        while (at > 0 && Compare(score, entries[row].Id, scores[at - 1], entries[rows[at - 1]].Id) < 0)
        {
            scores[at] = scores[at - 1];
            rows[at] = rows[at - 1];
            at--;
        }

        scores[at] = score;
        rows[at] = row;
        if (count < capacity)
            count++;
    }

    private static ScanRun Materialise(
        IReadOnlyList<QuantizedVectorEntry> entries,
        double[] scores,
        int[] rows,
        int count)
    {
        var ids = new string[count];
        var resultScores = new double[count];
        for (var rank = 0; rank < count; rank++)
        {
            ids[rank] = entries[rows[rank]].Id;
            resultScores[rank] = scores[rank];
        }

        return new ScanRun(ids, resultScores);
    }

    /// <summary>The shipped order as a comparison: negative when the left candidate ranks first.</summary>
    private static int Compare(double leftScore, string leftId, double rightScore, string rightId)
    {
        var byScore = rightScore.CompareTo(leftScore);
        return byScore != 0 ? byScore : string.CompareOrdinal(leftId, rightId);
    }

    private static ScanRun Harvest(IReadOnlyList<VectorSearchResult> results)
    {
        var ids = new string[results.Count];
        var scores = new double[results.Count];
        for (var rank = 0; rank < results.Count; rank++)
        {
            ids[rank] = results[rank].Id;
            scores[rank] = results[rank].Score;
        }

        return new ScanRun(ids, scores);
    }

    private static TimingRun Time(Func<int, ScanRun> scan, int queryCount, int warmup)
    {
        for (var round = 0; round < warmup; round++)
            for (var q = 0; q < queryCount; q++)
                GC.KeepAlive(scan(q));

        var ms = new double[queryCount];
        var runs = new ScanRun[queryCount];
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);

        for (var q = 0; q < queryCount; q++)
        {
            var start = Stopwatch.GetTimestamp();
            runs[q] = scan(q);
            ms[q] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var allocatedPerQuery = (GC.GetTotalAllocatedBytes(false) - allocatedBefore) / (double)queryCount;
        return new TimingRun(ms, runs, allocatedPerQuery);
    }

    private static ScanPathReport ToReport(
        string path,
        string description,
        TimingRun measurement,
        int rows,
        TimingRun? reference)
    {
        var mismatches = 0;
        var maxDelta = 0d;

        if (reference is { } baseline)
        {
            for (var q = 0; q < measurement.Runs.Length; q++)
            {
                var (mismatch, delta) = CompareRuns(baseline.Runs[q], measurement.Runs[q]);
                if (mismatch)
                    mismatches++;

                if (delta > maxDelta)
                    maxDelta = delta;
            }
        }

        var summary = LatencyStatistics.Summarize(measurement.Ms);
        return new ScanPathReport(
            path,
            description,
            summary.Count,
            summary.P50Ms,
            summary.P95Ms,
            summary.P99Ms,
            summary.MinMs,
            summary.MaxMs,
            summary.MeanMs,
            summary.P50Ms > 0 ? rows / (summary.P50Ms / 1000d) : double.NaN,
            measurement.AllocatedBytesPerQuery,
            mismatches,
            maxDelta,
            reference is not null && mismatches == 0 && maxDelta == 0d);
    }

    private static (bool Mismatch, double MaxDelta) CompareRuns(ScanRun shipped, ScanRun candidate)
    {
        var mismatch = shipped.Ids.Length != candidate.Ids.Length;
        var maxDelta = 0d;

        var count = Math.Min(shipped.Ids.Length, candidate.Ids.Length);
        for (var rank = 0; rank < count; rank++)
        {
            if (!string.Equals(shipped.Ids[rank], candidate.Ids[rank], StringComparison.Ordinal))
                mismatch = true;

            var delta = Math.Abs(shipped.Scores[rank] - candidate.Scores[rank]);
            if (delta > maxDelta)
                maxDelta = delta;
        }

        return (mismatch, maxDelta);
    }

    private static void PrintTable(IReadOnlyList<ScanPointReport> points, ScanAllocationReport allocation)
    {
        Console.WriteLine();
        Console.WriteLine("SCAN   SUMMARY (warm samples, scan only, no embedding)");
        Console.WriteLine("SCAN       rows  path                       p50        p95        p99     rows/s    mismatch  maxScoreDelta");
        foreach (var point in points)
        {
            foreach (var path in point.Paths)
            {
                Console.WriteLine(
                    $"SCAN   {point.Rows,7}  {path.Path,-24} {path.P50Ms,9:0.000} {path.P95Ms,9:0.000} "
                    + $"{path.P99Ms,9:0.000} {path.RowsPerSecond,11:0} {path.RankingMismatchesVsShipped,9} "
                    + $"{path.MaxScoreDeltaVsShipped,14:R}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"SCAN   ALLOC  rows={allocation.Rows} topK={allocation.TopK} reps={allocation.Repetitions} "
            + $"shipped={allocation.ShippedBytesPerQuery:0} B/query inplace={allocation.InPlaceBytesPerQuery:0} B/query "
            + $"share={allocation.InPlaceShareOfShipped * 100d:0.00}%");
    }

    private static float[][] BuildQueries(IReadOnlyList<float[]> poolFloats, int count, int dimensions, int noisePercent, int seed)
    {
        var rng = new Random(seed);
        var queries = new float[count][];

        for (var q = 0; q < count; q++)
        {
            var source = poolFloats[q * 37 % poolFloats.Count];
            var sigma = MaxAbs(source) * noisePercent / 100d;
            var buffer = new float[dimensions];
            for (var d = 0; d < dimensions; d++)
                buffer[d] = (float)(source[d] + (sigma * NextGaussian(rng)));

            queries[q] = VectorMath.Normalize(buffer);
        }

        return queries;
    }

    /// <summary>The component's own accumulation order, repeated here only to precompute the diagnostic's norms.</summary>
    private static double NormOf(QuantizedVector vector)
    {
        var codes = vector.Codes.Span;
        var scale = vector.Scale;

        double sum = 0d;
        for (var i = 0; i < codes.Length; i++)
        {
            var component = codes[i] * scale;
            sum += (double)component * component;
        }

        return Math.Sqrt(sum);
    }

    private static double MaxAbs(float[] vector)
    {
        var max = 0d;
        foreach (var value in vector)
            max = Math.Max(max, Math.Abs(value));

        return max;
    }

    private static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
