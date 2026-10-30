using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;
using PuddingCodeIndex.Storage;
using PuddingRetrievalEval.Contracts;
using PuddingRetrievalEval.Services;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// <c>--mode production</c>: run a frozen query set through the <b>production</b> retrieval port
/// (<see cref="IRetrievalPort"/>) and write three artefacts — <c>&lt;out&gt;.json</c> /
/// <c>&lt;out&gt;.md</c> (from <see cref="EvalRunner"/>, no bespoke metrics) and
/// <c>&lt;out&gt;.probe.json</c> (the environment-freeze block: data identity, repo identity, capability
/// tri-state, negative controls and the anchor/pagination/ref fixture probes).
/// <para>
/// <b>Why a snapshot instead of reading the live database.</b> The host writes
/// <c>D:\Data\databases\code-index\code_index.db</c> in WAL mode (background indexing). A "baseline"
/// measured against it drifts with unrelated indexing activity, so it is not reproducible even on the
/// same machine. The probe therefore takes a point-in-time copy through SQLite's online backup API
/// from a <c>Mode=ReadOnly</c> source connection and measures the copy; the copy's SHA-256 and byte
/// size go into the report, which is what makes "same data, same answer" checkable.
/// </para>
/// <para>
/// <b>Hard rule:</b> no code path in this mode may write to the production database. The store's
/// <see cref="SqliteCodeIndexStore.InitializeAsync"/> issues DDL/PRAGMAs, so it is only ever called on
/// the snapshot; pointing the probe at the production file is refused fail-closed, not silently
/// allowed.
/// </para>
/// </summary>
internal static class ProductionRun
{
    /// <summary>The live database the host writes. Only ever read through an online backup.</summary>
    internal const string ProductionDatabasePath = @"D:\Data\databases\code-index\code_index.db";

    /// <summary>
    /// Mirror of <c>SymbolRefIssuanceService.ProviderGeneration</c> ("codeindex-v1"). The probe does not
    /// reference PuddingRuntime, and issuing a ref with a *different* generation would produce tokens the
    /// production reader judges stale — so the value is copied deliberately, with this note.
    /// </summary>
    private const string SymbolRefProviderGeneration = "codeindex-v1";

    /// <summary>Default name suffix for the anchor fixture: a member the port itself calls.</summary>
    private const string DefaultAnchorSymbol = "ClassifyForEmpty";

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(ProbeOptions options, string repoRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workspace = options.Get("workspace") ?? "default";
        var projectFilter = options.Get("project");
        var setPath = options.Get("set")
                      ?? Path.Combine(repoRoot, "Source", "PuddingRetrievalEval", "eval", "sets", "production-negative-v1.json");
        var outBase = options.Get("out")
                      ?? Path.Combine(
                          repoRoot, "Source", "PuddingRetrievalEval", "eval", "reports",
                          "production-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var snapshotArgument = options.Get("snapshot");
        var dbArgument = options.Get("db");
        var anchorSymbolName = options.Get("anchor-symbol") ?? DefaultAnchorSymbol;
        var paginationQuery = options.Get("pagination-query") ?? "code";
        var paginationPageSize = Math.Clamp(options.GetInt("pagination-page-size", 3), 1, RetrievalBudget.MaxPageSize);
        var expectedUnder = options.Get("expected-under");
        var maxResults = options.GetInt("max-results", 20);
        var warmup = options.GetInt("warmup", 1);
        var measured = options.GetInt("measured", 3);

        var sourceDatabase = Path.GetFullPath(dbArgument ?? ProductionDatabasePath);
        var isProductionDatabase = PathsEqual(sourceDatabase, ProductionDatabasePath);
        var mustSnapshot = isProductionDatabase || snapshotArgument is not null;

        Console.WriteLine("mode          = production (real IRetrievalPort, no DI/Host)");
        Console.WriteLine($"workspace     = {workspace}");
        Console.WriteLine($"project       = {projectFilter ?? "<all registered projects>"}");
        Console.WriteLine($"set           = {setPath}");
        Console.WriteLine($"outBase       = {outBase}");
        Console.WriteLine($"db            = {sourceDatabase}{(isProductionDatabase ? " (live host database)" : string.Empty)}");
        Console.WriteLine($"snapshot      = {(snapshotArgument ?? (isProductionDatabase ? "<temp/r0-02-snapshot/<utc>>" : "<not needed>"))}");
        Console.WriteLine($"maxResults    = {maxResults}  warmup={warmup} measured={measured}");
        Console.WriteLine();

        // ---- 1. Freeze the data -------------------------------------------------------------
        var frozenDatabase = sourceDatabase;
        string? snapshotCreatedUtc = null;
        string? derivedFrom = null;

        if (mustSnapshot)
        {
            var snapshotDirectory = snapshotArgument is not null
                ? Path.GetFullPath(snapshotArgument)
                : Path.Combine(repoRoot, "temp", "r0-02-snapshot", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

            Directory.CreateDirectory(snapshotDirectory);
            frozenDatabase = Path.Combine(snapshotDirectory, "code_index.db");
            derivedFrom = sourceDatabase;

            var snapshotWatch = Stopwatch.StartNew();
            CreateOnlineSnapshot(sourceDatabase, frozenDatabase);
            snapshotWatch.Stop();
            snapshotCreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            var snapshotInfo = new FileInfo(frozenDatabase);
            Console.WriteLine(
                $"SNAPSHOT  {sourceDatabase} -> {frozenDatabase} "
                + $"({snapshotInfo.Length.ToString(CultureInfo.InvariantCulture)} bytes, "
                + $"{snapshotWatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms)");
            Console.WriteLine();
        }

        // Fail closed: the store's InitializeAsync issues DDL/PRAGMAs, which would write to the live file.
        if (PathsEqual(frozenDatabase, ProductionDatabasePath))
        {
            Console.Error.WriteLine(
                "refusing to open the live host database for measurement: "
                + "SqliteCodeIndexStore.InitializeAsync writes DDL/PRAGMAs. Pass --snapshot <dir> or --db <frozen copy>.");
            return 6;
        }

        var sourceBytes = new FileInfo(frozenDatabase).Length;
        var sourceSha256 = await ComputeSha256Async(frozenDatabase, cancellationToken).ConfigureAwait(false);

        // The store writes on open (DDL + journal_mode), so it must never see the frozen master: the
        // measurement runs on a throw-away working copy, and the master stays byte-identical so the
        // next run can prove "same snapshot SHA-256 ⇒ same answer".
        var workDirectory = Path.Combine(repoRoot, "temp", "r0-02-work", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workDirectory);
        var workDatabase = Path.Combine(workDirectory, "code_index.db");
        File.Copy(frozenDatabase, workDatabase, overwrite: true);

        Console.WriteLine($"SOURCE    frozen={frozenDatabase} bytes={sourceBytes} sha256={sourceSha256}");
        Console.WriteLine($"WORKCOPY  {workDatabase}");
        Console.WriteLine();

        // ---- 2. Drive the production port ---------------------------------------------------
        var store = new SqliteCodeIndexStore(workDatabase);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var port = new RetrievalPortV1(new CodeIndexStoreSymbolSource(store));
        var probe = new ProductionRetrievalProbe(port, workspace);

        var loaded = EvalSetLoader.LoadFromFile(setPath);
        var set = Program.RestrictCases(loaded, expectedUnder);

        var runOptions = new EvalRunOptions(
            new SearchScope(repoRoot, null),
            "workspace:" + workspace,
            maxResults,
            warmup,
            measured);

        var run = await new EvalRunner(probe).RunAsync(set, runOptions, cancellationToken).ConfigureAwait(false);

        var reproduce = "dotnet run --project Source/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- "
                        + $"--mode production --db \"{frozenDatabase}\" --workspace {workspace} "
                        + $"--set \"{setPath}\" --out \"{outBase}\" --max-results {maxResults} "
                        + $"--warmup {warmup} --measured {measured} --anchor-symbol {anchorSymbolName} "
                        + $"--pagination-query \"{paginationQuery}\" --pagination-page-size {paginationPageSize}";

        EvalReportWriter.Write(outBase, run, reproduce);

        Console.WriteLine($"RUN   probe={run.ProbeName} set={run.SetName} v{run.SetVersion} cases={run.CaseCount} failed={run.FailedCaseCount}");
        Console.WriteLine($"RUN   recall@1={run.RecallAt1:0.0000} recall@5={run.RecallAt5:0.0000} recall@10={run.RecallAt10:0.0000} MRR={run.Mrr:0.0000}");
        Console.WriteLine($"RUN   precision@5={run.PrecisionAt5:0.0000} precision@10={run.PrecisionAt10:0.0000} noiseRate@10={run.NoiseRateAt10:0.0000}");
        Console.WriteLine($"RUN   warm(n={run.WarmLatency.Count}) p50={run.WarmLatency.P50Ms:0.###} p95={run.WarmLatency.P95Ms:0.###} p99={run.WarmLatency.P99Ms:0.###}");
        Console.WriteLine($"RUN   totalMs={run.TotalElapsedMs} repetitionStable={run.AllRepetitionsIdentical}");
        Console.WriteLine();

        // ---- 3. Fixture probes (each one is a real call, not an assertion of intent) ---------
        var projects = await store.ListProjectsAsync(workspace, cancellationToken).ConfigureAwait(false);
        var (relationsBlock, anchorSymbol, relationCandidates) = await ProbeRelationsAsync(
            store, port, workspace, anchorSymbolName, projectFilter, cancellationToken).ConfigureAwait(false);
        var paginationBlock = await ProbePaginationAsync(
            port, workspace, paginationQuery, paginationPageSize, cancellationToken).ConfigureAwait(false);
        var symbolRefBlock = anchorSymbol is null
            ? new SymbolRefProbeBlock(false, null, false, 0, 0, null, null, false, null, [], 0, "没有可用的锚点符号：未进入签发分支。")
            : await ProbeSymbolRefAsync(store, projects, workspace, anchorSymbol, relationCandidates, paginationQuery, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"FIXTURE relations  ran={relationsBlock.Ran} anchor={relationsBlock.AnchorSymbol ?? "<none>"} incoming={relationsBlock.IncomingRelationCount} hits={relationsBlock.Hits} claim={relationsBlock.ClaimKind ?? "<none>"}");
        Console.WriteLine($"FIXTURE pagination ran={paginationBlock.Ran} page1={paginationBlock.Page1Hits} hasNext={paginationBlock.HasNextCursor} page2={paginationBlock.Page2Hits} overlap={paginationBlock.OverlapWithPage1}");
        Console.WriteLine($"FIXTURE symbolRef  ran={symbolRefBlock.Ran} attempts={symbolRefBlock.Attempts} issued={symbolRefBlock.IssuedCount} prefix={symbolRefBlock.TokenPrefix ?? "<none>"} issuedSymbol={symbolRefBlock.IssuedSymbol ?? "<none>"} anchorIssued={symbolRefBlock.AnchorIssued}");
        Console.WriteLine();

        // ---- 4. Capability tri-state + negative controls ------------------------------------
        var observedClaims = probe.ObservedClaims;
        var capabilities = ProductionCapabilityPolicy.Build(new ProbeCapabilityEvidence(
            SuccessfulCalls: run.CaseCount - run.FailedCaseCount,
            Claims: observedClaims.Values.ToArray(),
            RelationsFaceExecuted: relationsBlock.Ran,
            VectorFaceAvailable: probe.SupportsVectorFace));

        var negativeControls = BuildNegativeControls(run, observedClaims);
        var repoBlock = await ReadRepoBlockAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        var setSha256 = await ComputeSha256Async(setPath, cancellationToken).ConfigureAwait(false);
        var sourceSha256AfterRun = await ComputeSha256Async(frozenDatabase, cancellationToken).ConfigureAwait(false);

        var report = new ProductionProbeReport(
            ProducedAtUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Probe: probe.Name,
            Source: new SourceBlock(
                "code-index",
                frozenDatabase,
                sourceSha256,
                sourceBytes,
                derivedFrom,
                snapshotCreatedUtc,
                workDatabase,
                sourceSha256AfterRun,
                string.Equals(sourceSha256, sourceSha256AfterRun, StringComparison.OrdinalIgnoreCase)),
            Repo: repoBlock,
            Set: new SetBlock(set.Name, set.Version, set.Cases.Count, setSha256),
            Capabilities: capabilities,
            NegativeControls: negativeControls,
            ClaimStability: new ClaimStabilityBlock(
                observedClaims.Count,
                probe.ClaimVariationQueries.Count,
                probe.ZeroHitCallsWithoutClaim,
                run.AllRepetitionsIdentical),
            FixtureProbes: new FixtureProbeBlock(relationsBlock, paginationBlock, symbolRefBlock),
            Notes:
            [
                "capabilities 三态来自实际执行分支：lexical 依据本轮 claim.searched 是否恒含 lexical；relations 依据锚点关系意图是否真的调用过；vector 在本构建不可执行（R5 未落地），故不出现任何向量指标数值。",
                "本模式只读：数据经 SQLite 在线备份 API 从 Mode=ReadOnly 连接复制成冻结快照；测量在一次性工作副本上进行，冻结主干只被读取，跑完重算 sha256 与跑前一致（见 source.frozenUnchanged）。对生产库没有任何写语句。",
                "fixtureProbes 中的行号/命中数是**本次冻结快照上的实测值**，不是永久断言；换快照或换 HEAD 必须重测。",
            ]);

        var probeJsonPath = outBase + ".probe.json";
        var probeJsonDirectory = Path.GetDirectoryName(Path.GetFullPath(probeJsonPath));
        if (!string.IsNullOrEmpty(probeJsonDirectory))
            Directory.CreateDirectory(probeJsonDirectory);

        await File.WriteAllTextAsync(
            probeJsonPath,
            JsonSerializer.Serialize(report, ReportJsonOptions),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"RUN   wrote {outBase}.md, {outBase}.json and {probeJsonPath}");
        Console.WriteLine($"CAPS  lexical={capabilities.Lexical.State} relations={capabilities.Relations.State} vector={capabilities.Vector.State}");
        Console.WriteLine($"NEG   controls={negativeControls.Count} passed={negativeControls.Count(c => c.Passed)}");
        Console.WriteLine($"CLAIM queries={observedClaims.Count} varied={probe.ClaimVariationQueries.Count} zeroHitWithoutClaim={probe.ZeroHitCallsWithoutClaim}");

        return run.FailedCaseCount == 0 ? 0 : 4;
    }

    /// <summary>
    /// Point-in-time copy through SQLite's online backup API. The source connection is
    /// <see cref="SqliteOpenMode.ReadOnly"/>, so the backup API cannot write to the live file even by
    /// accident; concurrent host commits keep appending to the WAL and are simply not in the copy.
    /// </summary>
    private static void CreateOnlineSnapshot(string sourceDatabasePath, string snapshotPath)
    {
        if (!File.Exists(sourceDatabasePath))
            throw new FileNotFoundException($"source database not found: {sourceDatabasePath}", sourceDatabasePath);

        // A stale sidecar next to an old snapshot would be replayed into the new copy.
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var stale = snapshotPath + suffix;
            if (File.Exists(stale))
                File.Delete(stale);
        }

        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>Streams the file through SHA-256 (the database is ~1.8 GiB, so 1 MiB chunks, no full read into memory).</summary>
    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 20,
            useAsync: true);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Relations fixture: locate a member that really has incoming edges (by identity data in the
    /// index), then measure the relations face <b>through the port</b>. Locating the anchor by name is
    /// not a retrieval measurement — it is the same identity resolution the tool layer performs before
    /// it hands an anchor to the port.
    /// </summary>
    private static async Task<(RelationsProbeBlock Block, CodeSymbolRecord? Anchor, IReadOnlyList<CodeSymbolRecord> Candidates)> ProbeRelationsAsync(
        ICodeIndexStore store,
        IRetrievalPort port,
        string workspace,
        string symbolName,
        string? projectFilter,
        CancellationToken cancellationToken)
    {
        const int maxAnchorCandidates = 5;

        try
        {
            var candidates = await store.SearchSymbolsAsync(
                new CodeSymbolSearchRequest(
                    WorkspaceId: workspace,
                    Query: symbolName,
                    ProjectId: projectFilter,
                    Kind: null,
                    Limit: 50,
                    Skip: 0,
                    MatchTarget: CodeSymbolMatchTarget.Name),
                cancellationToken).ConfigureAwait(false);

            // Pass 1 — collect members that really have incoming edges. Only identity data is read
            // here; nothing is measured yet, so the port call below stays the single retrieval event.
            var withEdges = new List<(CodeSymbolRecord Symbol, int EdgeCount)>();

            foreach (var candidate in candidates
                         .Where(c => c.Kind is CodeSymbolKind.Method or CodeSymbolKind.Property or CodeSymbolKind.Constructor)
                         .Take(25))
            {
                var edges = await store.ListIncomingRelationsAsync(
                    workspace,
                    candidate.ProjectId,
                    candidate.SymbolId,
                    relationKind: null,
                    relationKinds: [CodeRelationKind.Calls, CodeRelationKind.References],
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (edges.Count > 0)
                    withEdges.Add((candidate, edges.Count));

                if (withEdges.Count >= maxAnchorCandidates)
                    break;
            }

            if (withEdges.Count == 0)
            {
                return (new RelationsProbeBlock(
                    false,
                    null,
                    null,
                    RetrievalIntent.Callers.ToString(),
                    0,
                    0,
                    null,
                    $"索引里没有名为 '{symbolName}' 且带入边（Calls/References）的成员锚点：扫描 {candidates.Count} 个同名候选。"), null, []);
            }

            var anchor = withEdges[0];

            var request = new RetrievalRequest(
                query: string.Empty,
                scopeId: workspace,
                intent: RetrievalIntent.Callers,
                pageSize: 20,
                anchor: new SymbolAnchor(anchor.Symbol.ProjectId, anchor.Symbol.SymbolId));

            var result = await port.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

            var block = new RelationsProbeBlock(
                Ran: true,
                AnchorSymbol: anchor.Symbol.Name,
                AnchorSource: $"{anchor.Symbol.FilePath}:{anchor.Symbol.StartLine.ToString(CultureInfo.InvariantCulture)}",
                Intent: RetrievalIntent.Callers.ToString(),
                IncomingRelationCount: anchor.EdgeCount,
                Hits: result.Hits.Count,
                ClaimKind: result.Claim?.Kind.ToString(),
                Note: result.Hits.Count == 0
                    ? "关系面 0 命中：claim 已给出否定类型，本探针不做进一步推断。"
                    : null);

            return (block, anchor.Symbol, withEdges.Select(entry => entry.Symbol).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (new RelationsProbeBlock(
                false, null, null, RetrievalIntent.Callers.ToString(), 0, 0, null,
                $"关系面测量失败（{ex.GetType().Name}: {ex.Message}）"), null, []);
        }
    }

    /// <summary>Pagination fixture: first page must hand back a cursor, and the cursor must yield a
    /// non-overlapping second page (a cursor that repeats page 1 is worse than no cursor).</summary>
    private static async Task<PaginationProbeBlock> ProbePaginationAsync(
        IRetrievalPort port,
        string workspace,
        string query,
        int pageSize,
        CancellationToken cancellationToken)
    {
        try
        {
            var page1 = await port.RetrieveAsync(
                new RetrievalRequest(query, workspace, RetrievalIntent.Auto, pageSize: pageSize),
                cancellationToken).ConfigureAwait(false);

            if (page1.NextCursor is null)
            {
                return new PaginationProbeBlock(
                    true, query, pageSize, page1.Hits.Count, false, 0, 0,
                    $"首屏即完整（totalCount={page1.TotalCount.ToString(CultureInfo.InvariantCulture)} ≤ pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}）：无下一页可取。");
            }

            var page2 = await port.RetrieveAsync(
                new RetrievalRequest(query, workspace, RetrievalIntent.Auto, pageSize: pageSize, cursor: page1.NextCursor),
                cancellationToken).ConfigureAwait(false);

            var page1Keys = page1.Hits.Select(hit => hit.Identity.Key).ToHashSet(StringComparer.Ordinal);
            var overlap = page2.Hits.Count(hit => page1Keys.Contains(hit.Identity.Key));

            return new PaginationProbeBlock(
                true, query, pageSize, page1.Hits.Count, true, page2.Hits.Count, overlap,
                overlap == 0 ? null : "第二页与第一页有重复身份：游标未推进（缺陷）。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PaginationProbeBlock(
                false, query, pageSize, 0, false, 0, 0,
                $"分页测量失败（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    /// <summary>
    /// symbol_ref fixture: issue a ref through the production issuer (manifest fingerprints only —
    /// never the current disk hash, which would sign a ref that looks fresh but points at a newer file).
    /// <para>
    /// The anchor symbol is only the first attempt. Production refuses to sign a token longer than 512
    /// bytes (long symbol ids), so a single attempt would report "cannot sign" while the channel
    /// actually works for other symbols. The fixture therefore tries the anchor first, then the other
    /// relation-bearing candidates, and reports the first real token plus every skip reason.
    /// </para>
    /// </summary>
    private static async Task<SymbolRefProbeBlock> ProbeSymbolRefAsync(
        ICodeIndexStore store,
        IReadOnlyList<CodeProjectRecord> projects,
        string workspace,
        CodeSymbolRecord anchor,
        IReadOnlyList<CodeSymbolRecord> relationCandidates,
        string sweepQuery,
        CancellationToken cancellationToken)
    {
        if (store is not ICodeSourceMaintenanceStore maintenance)
        {
            return new SymbolRefProbeBlock(false, null, false, 0, 0, null, null, false, null, [], 0, "存储未实现 ICodeSourceMaintenanceStore：无法读取 manifest，拒绝签发。");
        }

        // The anchor comes first, then a broad sample. Production refuses a token longer than 512 bytes,
        // which long symbol ids hit; trying a single symbol would report "the channel is broken" while
        // it actually works for other symbols — and trying only symbols that work would hide the cap.
        // So: sample a fixed pool, count outcomes, report the first real token *and* every skip reason.
        var pool = new List<CodeSymbolRecord> { anchor };
        pool.AddRange(relationCandidates.Where(c => !string.Equals(c.SymbolId, anchor.SymbolId, StringComparison.Ordinal)));

        try
        {
            var sweep = await store.SearchSymbolsAsync(
                new CodeSymbolSearchRequest(
                    WorkspaceId: workspace,
                    Query: sweepQuery,
                    ProjectId: null,
                    Kind: null,
                    Limit: 40,
                    Skip: 0,
                    MatchTarget: CodeSymbolMatchTarget.Name),
                cancellationToken).ConfigureAwait(false);

            const int extraSamples = 20;
            pool.AddRange(sweep
                .Where(c => c.Kind is not CodeSymbolKind.Namespace)
                .Take(extraSamples));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SymbolRefProbeBlock(false, null, false, 0, 0, null, null, false, null, [], 0, $"候选采样失败（{ex.GetType().Name}: {ex.Message}）");
        }

        var skipReasons = new List<string>();
        var issuersByProject = new Dictionary<string, SymbolRefIssuer>(StringComparer.Ordinal);
        var attempts = 0;
        var issuedCount = 0;
        SymbolRefIssue? anchorIssue = null;
        string? firstToken = null;
        CodeSymbolRecord? firstIssuedSymbol = null;

        try
        {
            foreach (var candidate in pool)
            {
                if (!issuersByProject.TryGetValue(candidate.ProjectId, out var issuer))
                {
                    var snapshot = await maintenance
                        .LoadSourceMaintenanceAsync(workspace, candidate.ProjectId, cancellationToken)
                        .ConfigureAwait(false);

                    var projectRoot = projects
                        .FirstOrDefault(p => string.Equals(p.ProjectId, candidate.ProjectId, StringComparison.Ordinal))
                        ?.ProjectPath;

                    issuer = SymbolRefIssuer.FromManifest(snapshot.Manifest, projectRoot);
                    issuersByProject[candidate.ProjectId] = issuer;
                }

                attempts++;

                var issue = issuer.Issue(
                    candidate.ProjectId,
                    SymbolRefProviderGeneration,
                    new SymbolRefIssueRequest(candidate.FilePath, candidate.SymbolId, candidate.StartLine, candidate.EndLine));

                if (string.Equals(candidate.SymbolId, anchor.SymbolId, StringComparison.Ordinal))
                    anchorIssue = issue;

                if (!issue.Issued)
                {
                    if (skipReasons.Count < 5)
                        skipReasons.Add($"{candidate.Name}: {issue.SkipReason}");

                    continue;
                }

                issuedCount++;

                if (firstToken is null)
                {
                    firstToken = issue.Token;
                    firstIssuedSymbol = candidate;
                }
            }

            var prefix = firstToken is not null && firstToken.StartsWith(SymbolRefV1.TokenPrefix, StringComparison.Ordinal)
                ? SymbolRefV1.TokenPrefix
                : null;

            var anchorKnownPaths = issuersByProject.TryGetValue(anchor.ProjectId, out var anchorIssuer)
                ? anchorIssuer.KnownPathCount
                : 0;

            return new SymbolRefProbeBlock(
                Ran: true,
                TokenPrefix: prefix,
                Issued: issuedCount > 0,
                Attempts: attempts,
                IssuedCount: issuedCount,
                IssuedSymbol: firstIssuedSymbol?.Name,
                IssuedSymbolSource: firstIssuedSymbol is null
                    ? null
                    : $"{firstIssuedSymbol.FilePath}:{firstIssuedSymbol.StartLine.ToString(CultureInfo.InvariantCulture)}",
                AnchorIssued: anchorIssue?.Issued ?? false,
                AnchorSkipReason: anchorIssue?.SkipReason,
                SkipReasons: skipReasons,
                ManifestKnownPaths: anchorKnownPaths,
                Note: issuedCount == 0
                    ? "抽样内没有签出任何 pmsym1.* token：每条拒绝原因都在 skipReasons 里（这是实测事实，不是通过）。"
                    : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SymbolRefProbeBlock(false, null, false, attempts, issuedCount, null, null, false, null, skipReasons, 0, $"签发测量失败（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    /// <summary>
    /// Negative controls: every case that returned nothing, quoted with the claim the port actually
    /// produced. <c>passed</c> is the gate wording — <c>claim.kind == None</c> and the unsearched list
    /// still names the vector face, i.e. "lexical only" is never allowed to read as "it does not exist".
    /// </summary>
    private static IReadOnlyList<NegativeControlEntry> BuildNegativeControls(
        EvalRun run,
        IReadOnlyDictionary<string, SearchProbeClaim> claims)
    {
        var entries = new List<NegativeControlEntry>();

        foreach (var score in run.Cases)
        {
            if (score.RankedHits.Count > 0)
                continue;

            if (!claims.TryGetValue(score.Case.Query, out var claim))
                continue;

            var passed = string.Equals(claim.Kind, RetrievalClaimKind.None.ToString(), StringComparison.Ordinal)
                         && claim.Unsearched.Contains(RetrievalClaim.SurfaceVector, StringComparer.Ordinal);

            entries.Add(new NegativeControlEntry(
                Query: score.Case.Query,
                ClaimKind: claim.Kind,
                Unsearched: claim.Unsearched,
                Passed: passed,
                CaseKind: score.Case.Kind.ToString()));
        }

        return entries;
    }

    /// <summary>
    /// Repo identity of the measurement. Reads git through the CLI rather than guessing from
    /// <c>.git</c> layout; when git is unavailable the block says so instead of claiming cleanliness.
    /// </summary>
    private static async Task<RepoBlock> ReadRepoBlockAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var head = await RunGitAsync(repoRoot, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (head is null)
            return new RepoBlock(null, null, null, 0, "git 不可用或该目录不是仓库：worktree 状态未证实，本节留空（不猜）。");

        var subject = await RunGitAsync(repoRoot, ["log", "-1", "--pretty=%s"], cancellationToken).ConfigureAwait(false);
        var status = await RunGitAsync(repoRoot, ["status", "--porcelain=v1"], cancellationToken).ConfigureAwait(false);

        var dirtyCount = status is null
            ? 0
            : status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        return new RepoBlock(
            HeadSha: head.Trim(),
            HeadSubject: subject?.Trim(),
            WorktreeClean: status is null ? null : dirtyCount == 0,
            DirtyPathCount: dirtyCount,
            Note: status is null ? "读取 git status 失败：worktree 是否干净未证实。" : null);
    }

    private static async Task<string?> RunGitAsync(
        string repoRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    // ---- .probe.json shape (field names are the contract; keep them stable) ------------------

    internal sealed record ProductionProbeReport(
        [property: JsonPropertyName("producedAtUtc")] string ProducedAtUtc,
        [property: JsonPropertyName("probe")] string Probe,
        [property: JsonPropertyName("source")] SourceBlock Source,
        [property: JsonPropertyName("repo")] RepoBlock Repo,
        [property: JsonPropertyName("set")] SetBlock Set,
        [property: JsonPropertyName("capabilities")] ProbeCapabilities Capabilities,
        [property: JsonPropertyName("negativeControls")] IReadOnlyList<NegativeControlEntry> NegativeControls,
        [property: JsonPropertyName("claimStability")] ClaimStabilityBlock ClaimStability,
        [property: JsonPropertyName("fixtureProbes")] FixtureProbeBlock FixtureProbes,
        [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes);

    internal sealed record SourceBlock(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("pathOrSnapshot")] string PathOrSnapshot,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("bytes")] long Bytes,
        [property: JsonPropertyName("derivedFrom")] string? DerivedFrom,
        [property: JsonPropertyName("snapshotCreatedUtc")] string? SnapshotCreatedUtc,
        [property: JsonPropertyName("workCopy")] string WorkCopy,
        [property: JsonPropertyName("sha256AfterRun")] string Sha256AfterRun,
        [property: JsonPropertyName("frozenUnchanged")] bool FrozenUnchanged);

    internal sealed record RepoBlock(
        [property: JsonPropertyName("headSha")] string? HeadSha,
        [property: JsonPropertyName("headSubject")] string? HeadSubject,
        [property: JsonPropertyName("worktreeClean")] bool? WorktreeClean,
        [property: JsonPropertyName("dirtyPathCount")] int DirtyPathCount,
        [property: JsonPropertyName("note")] string? Note);

    internal sealed record SetBlock(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("caseCount")] int CaseCount,
        [property: JsonPropertyName("sha256")] string Sha256);

    internal sealed record NegativeControlEntry(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("claimKind")] string ClaimKind,
        [property: JsonPropertyName("unsearched")] IReadOnlyList<string> Unsearched,
        [property: JsonPropertyName("passed")] bool Passed,
        [property: JsonPropertyName("caseKind")] string CaseKind);

    internal sealed record ClaimStabilityBlock(
        [property: JsonPropertyName("queriesWithClaim")] int QueriesWithClaim,
        [property: JsonPropertyName("variedQueries")] int VariedQueries,
        [property: JsonPropertyName("zeroHitCallsWithoutClaim")] int ZeroHitCallsWithoutClaim,
        [property: JsonPropertyName("repetitionsIdentical")] bool RepetitionsIdentical);

    internal sealed record FixtureProbeBlock(
        [property: JsonPropertyName("relations")] RelationsProbeBlock Relations,
        [property: JsonPropertyName("pagination")] PaginationProbeBlock Pagination,
        [property: JsonPropertyName("symbolRef")] SymbolRefProbeBlock SymbolRef);

    internal sealed record RelationsProbeBlock(
        [property: JsonPropertyName("ran")] bool Ran,
        [property: JsonPropertyName("anchorSymbol")] string? AnchorSymbol,
        [property: JsonPropertyName("anchorSource")] string? AnchorSource,
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("incomingRelationCount")] int IncomingRelationCount,
        [property: JsonPropertyName("hits")] int Hits,
        [property: JsonPropertyName("claimKind")] string? ClaimKind,
        [property: JsonPropertyName("note")] string? Note);

    internal sealed record PaginationProbeBlock(
        [property: JsonPropertyName("ran")] bool Ran,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("page1Hits")] int Page1Hits,
        [property: JsonPropertyName("hasNextCursor")] bool HasNextCursor,
        [property: JsonPropertyName("page2Hits")] int Page2Hits,
        [property: JsonPropertyName("overlapWithPage1")] int OverlapWithPage1,
        [property: JsonPropertyName("note")] string? Note);

    internal sealed record SymbolRefProbeBlock(
        [property: JsonPropertyName("ran")] bool Ran,
        [property: JsonPropertyName("tokenPrefix")] string? TokenPrefix,
        [property: JsonPropertyName("issued")] bool Issued,
        [property: JsonPropertyName("attempts")] int Attempts,
        [property: JsonPropertyName("issuedCount")] int IssuedCount,
        [property: JsonPropertyName("issuedSymbol")] string? IssuedSymbol,
        [property: JsonPropertyName("issuedSymbolSource")] string? IssuedSymbolSource,
        [property: JsonPropertyName("anchorIssued")] bool AnchorIssued,
        [property: JsonPropertyName("skipReason")] string? AnchorSkipReason,
        [property: JsonPropertyName("skipReasons")] IReadOnlyList<string> SkipReasons,
        [property: JsonPropertyName("manifestKnownPaths")] int ManifestKnownPaths,
        [property: JsonPropertyName("note")] string? Note);
}
