using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;

namespace PuddingFullTextIndex.Cli;

/// <summary>退出码契约（规格 §3 R2）：0 成功 / 2 被拒 / 3 本进程无法完成 / 4 执行未成功 / 5 用法错误。</summary>
internal static class SupplyCliExitCodes
{
    internal const int Success = 0;
    internal const int Rejected = 2;
    internal const int Busy = 3;
    internal const int NotSuccessful = 4;
    internal const int UsageError = 5;
}

/// <summary>
/// 供给 CLI 核心（可注入 <see cref="TextWriter"/> 与 <see cref="SupplyCliHost"/> ⇒ 单测不必起进程）。
/// <para>
/// 命令：<c>plan</c>（干跑，零写入）/ <c>status</c>（如实报告磁盘可观察状态）/
/// <c>build</c>（真实引擎 + 显式索引根）/ <c>cancel</c>（跨进程时如实失败）。
/// </para>
/// </summary>
public static class SupplyCli
{
    private const string RequestedBy = "PuddingFullTextIndex.Cli";

    /// <summary>job 状态存储是**进程内**的：本 CLI 每次调用都是新进程 ⇒ 外来的 jobId 必然查不到。</summary>
    private const string NotInThisProcess = "not_in_this_process";

    private const string NotInThisProcessDetail =
        "job 状态存储是进程内的（协调器 A1 的内存台账）：本进程看不到该 jobId，"
        + "因此本进程既不据此外推它的状态，也无法取消它。跨进程 job 台账属后续切片。";

    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr) =>
        RunAsync(args, stdout, stderr, new SupplyCliHost());

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, SupplyCliHost host)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(host);

        var parse = SupplyCommandLine.Parse(args);
        if (!parse.Succeeded)
        {
            stderr.WriteLine($"usage-error: {parse.Error}");
            stderr.WriteLine();
            stderr.WriteLine(SupplyCommandLine.UsageText);
            return SupplyCliExitCodes.UsageError;
        }

        var options = parse.Options!;
        try
        {
            return options.Command switch
            {
                SupplyCliCommand.Plan => await RunPlanAsync(options, host, stdout).ConfigureAwait(false),
                SupplyCliCommand.Status => await RunStatusAsync(options, host, stdout).ConfigureAwait(false),
                SupplyCliCommand.Build => await RunBuildAsync(options, host, stdout).ConfigureAwait(false),
                SupplyCliCommand.Cancel => await RunCancelAsync(options, host, stdout).ConfigureAwait(false),
                _ => SupplyCliExitCodes.UsageError,
            };
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("error: 操作被取消（CancellationToken 已触发），不视为成功。");
            return SupplyCliExitCodes.NotSuccessful;
        }
        catch (Exception ex)
        {
            // 绝不把异常吞成成功：如实打到 stderr 并返回非零。
            stderr.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return SupplyCliExitCodes.NotSuccessful;
        }
    }

    // ── plan：干跑，零写入 ─────────────────────────────────────────────────

    private static async Task<int> RunPlanAsync(SupplyCliOptions cli, SupplyCliHost host, TextWriter stdout)
    {
        var (indexOptions, indexRoot, indexRootExplicit) = ResolveIndexOptions(cli);
        var composition = Compose(host, indexOptions);
        var coordinator = host.CreateCoordinator(composition);

        // ⚠️ 零写入由 A1 的 PlanAsync 保证（不建索引目录、不写租约、不触发构建）——
        // 本命令不得在此之外碰任何磁盘路径。
        var plan = await coordinator
            .PlanAsync(new SupplyScopeRequest(cli.Scopes, cli.BudgetBytes, RequestedBy))
            .ConfigureAwait(false);

        var dto = new PlanJson(
            Command: "plan",
            IndexRoot: indexRoot,
            IndexRootExplicit: indexRootExplicit,
            Owner: composition.CoordinatorOptions.OwnerId,
            Accepted: plan.Accepted,
            BudgetBytes: plan.BudgetBytes,
            IndexSizeFactor: plan.IndexSizeFactor,
            Scopes: plan.AcceptedScopes
                .Select(s => new ScopeEstimateJson(
                    s.ScopeKey, s.RootPath, s.FileCount, s.CorpusBytes, s.PredictedIndexBytes, s.BudgetBytes, s.WithinBudget))
                .ToList(),
            Rejections: plan.RejectedScopes
                .Select(r => new RejectionJson(r.Value, r.Reason.ToString(), r.Message))
                .ToList(),
            // 任一拒绝项 ⇒ 2；预算超限只做报表（硬限属 A2），不影响退出码。
            ExitCode: plan.RejectedScopes.Count > 0 ? SupplyCliExitCodes.Rejected : SupplyCliExitCodes.Success);

        SupplyCliViews.RenderPlan(stdout, cli.Json, dto);
        return dto.ExitCode;
    }

    // ── status：如实报告磁盘可观察状态 ─────────────────────────────────────

    private static async Task<int> RunStatusAsync(SupplyCliOptions cli, SupplyCliHost host, TextWriter stdout)
    {
        var (indexOptions, indexRoot, indexRootExplicit) = ResolveIndexOptions(cli);
        var composition = Compose(host, indexOptions);
        var coordinator = host.CreateCoordinator(composition);

        JobLookupJson? lookup = null;
        if (cli.JobId is { Length: > 0 } jobId)
        {
            var status = await coordinator.GetStatusAsync(jobId).ConfigureAwait(false);
            lookup = status is null
                ? new JobLookupJson(jobId, NotInThisProcess, NotInThisProcessDetail, Job: null)
                : new JobLookupJson(jobId, "in_this_process", "本进程 job 台账命中。", JobStatusJson.From(status));
        }

        var jobs = (await coordinator.ListStatusAsync().ConfigureAwait(false))
            .Select(JobStatusJson.From)
            .ToList();

        var scopes = new List<StatusScopeJson>(cli.Scopes.Count);
        foreach (var rawScope in cli.Scopes)
            scopes.Add(await ObserveScopeAsync(composition, indexRoot, rawScope).ConfigureAwait(false));

        var (rootExists, rootEntries) = ObserveTopLevel(indexRoot);

        var dto = new StatusJson(
            Command: "status",
            IndexRoot: indexRoot,
            IndexRootExplicit: indexRootExplicit,
            Owner: composition.CoordinatorOptions.OwnerId,
            IndexRootExists: rootExists,
            IndexRootTopLevelEntries: rootEntries,
            JobLookup: lookup,
            Jobs: jobs,
            Scopes: scopes,
            // status 只做只读观察：观察本身成败即退出码 0（用法错误已在解析期返回 5）。
            ExitCode: SupplyCliExitCodes.Success);

        SupplyCliViews.RenderStatus(stdout, cli.Json, dto);
        return dto.ExitCode;
    }

    private static async Task<StatusScopeJson> ObserveScopeAsync(
        SupplyCliComposition composition,
        string indexRoot,
        string rawScope)
    {
        string scopePath;
        try
        {
            scopePath = SupplyScopeMirror.NormalizeRoot(rawScope);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 无法规范化 ⇒ 如实报告"无法解析"，不猜、不假装观察过。
            return new StatusScopeJson(
                ScopePath: rawScope,
                ScopeKey: "unresolvable",
                ScopeExists: false,
                HasIndex: false,
                IndexDirectory: string.Empty,
                IndexDirectoryExists: false,
                IndexEntryCount: 0,
                IndexBytes: 0,
                LeaseLookup: "unresolvable",
                LeaseHolder: null);
        }

        var scopeKey = SupplyScopeMirror.ToScopeKey(scopePath);
        // A19：命名哈希不再由 CLI 复刻，直接问组件单一真源（与引擎 build/search 同一条实现）。
        var indexDirectory = FullTextIndexPaths.ResolveIndexDirectory(indexRoot, scopePath);
        var (dirExists, entries, bytes) = ObserveDirectory(indexDirectory);
        var holder = await composition.Lease.DescribeHolderAsync(scopeKey).ConfigureAwait(false);

        return new StatusScopeJson(
            ScopePath: scopePath,
            ScopeKey: scopeKey,
            ScopeExists: Directory.Exists(scopePath),
            // HasIndex 走引擎的公开判定（与 build/search 同口径），不靠 CLI 自己推测。
            HasIndex: composition.Engine.HasIndex(scopePath),
            IndexDirectory: indexDirectory,
            IndexDirectoryExists: dirExists,
            IndexEntryCount: entries,
            IndexBytes: bytes,
            LeaseLookup: holder is null ? "none" : "holder",
            LeaseHolder: HolderJson.From(holder));
    }

    // ── build：真实引擎 + 显式索引根 ───────────────────────────────────────

    private static async Task<int> RunBuildAsync(SupplyCliOptions cli, SupplyCliHost host, TextWriter stdout)
    {
        var (indexOptions, indexRoot, indexRootExplicit) = ResolveIndexOptions(cli);
        var composition = Compose(host, indexOptions);
        var coordinator = host.CreateCoordinator(composition);

        var outcome = await coordinator
            .BuildAsync(new SupplyScopeRequest(cli.Scopes, cli.BudgetBytes, RequestedBy))
            .ConfigureAwait(false);

        var exitCode = outcome.Outcome switch
        {
            SupplyOutcome.Rejected => SupplyCliExitCodes.Rejected,
            SupplyOutcome.Busy => SupplyCliExitCodes.Busy,
            // Started/Merged 都只是"已受理"：真正的成败由 --wait 的终态决定。
            SupplyOutcome.Started or SupplyOutcome.Merged => SupplyCliExitCodes.Success,
            _ => SupplyCliExitCodes.NotSuccessful,
        };

        BuildWaitOutcome? waited = null;
        if (cli.Wait
            && outcome.Outcome is SupplyOutcome.Started or SupplyOutcome.Merged
            && outcome.JobId is { Length: > 0 } jobId)
        {
            waited = await WaitForTerminalAsync(coordinator, host, composition.Recorder, jobId).ConfigureAwait(false);
            exitCode = waited.ExitCode;
        }

        var dto = new BuildJson(
            Command: "build",
            IndexRoot: indexRoot,
            IndexRootExplicit: indexRootExplicit,
            Owner: composition.CoordinatorOptions.OwnerId,
            Outcome: outcome.Outcome.ToString(),
            JobId: outcome.JobId,
            Reason: outcome.Reason,
            Holder: HolderJson.From(outcome.Holder),
            Scopes: outcome.Scopes
                .Select(s => new ScopeOutcomeJson(
                    s.Value, s.ScopeKey, s.Outcome.ToString(), s.JobId, s.Reason, HolderJson.From(s.Holder)))
                .ToList(),
            Waited: waited is not null,
            State: waited?.Status?.State.ToString(),
            Phase: waited?.Status?.Phase,
            WaitNote: waited?.Note,
            IndexedFileCount: waited?.IndexedFileCount,
            TotalBytes: waited?.TotalBytes,
            ElapsedMs: waited?.ElapsedMs,
            MetricsSource: waited?.MetricsSource,
            Message: waited?.Status?.Message,
            ExitCode: exitCode);

        SupplyCliViews.RenderBuild(stdout, cli.Json, dto);
        return dto.ExitCode;
    }

    /// <summary>
    /// 轮询到终态并映射退出码。三种"没到成功终态"的路径都必须如实、非零：
    /// 本进程看不到 job（⇒ <c>not_in_this_process</c>）、等待超时、终态为 Failed/Cancelled。
    /// </summary>
    private static async Task<BuildWaitOutcome> WaitForTerminalAsync(
        IFullTextIndexSupplyCoordinator coordinator,
        SupplyCliHost host,
        RecordingIndexBuilder recorder,
        string jobId)
    {
        var deadline = DateTimeOffset.UtcNow + host.BuildTimeoutValue;

        while (true)
        {
            var status = await coordinator.GetStatusAsync(jobId).ConfigureAwait(false);

            if (status is null)
                return new BuildWaitOutcome(SupplyCliExitCodes.Busy, null, NotInThisProcess, null, null, null, null);

            if (IsTerminal(status.State))
                return Describe(status, recorder, MapTerminalExitCode(status.State), note: null);

            if (DateTimeOffset.UtcNow >= deadline)
                return Describe(status, recorder, SupplyCliExitCodes.Busy, note: "wait-timeout");

            await host.WaitAsync(host.WaitPollIntervalValue, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool IsTerminal(SupplyJobState state) =>
        state is SupplyJobState.Succeeded or SupplyJobState.Failed or SupplyJobState.Cancelled;

    /// <summary>
    /// 终态 → 退出码。
    /// ⚠️ M2 变异点：把 <see cref="SupplyJobState.Failed"/> 改成 <c>Success</c> 即"吞掉失败"，A3 必红。
    /// </summary>
    private static int MapTerminalExitCode(SupplyJobState state) => state switch
    {
        SupplyJobState.Succeeded => SupplyCliExitCodes.Success,
        SupplyJobState.Failed => SupplyCliExitCodes.NotSuccessful,
        // 取消不是成功：也不得返回 0。
        SupplyJobState.Cancelled => SupplyCliExitCodes.NotSuccessful,
        _ => SupplyCliExitCodes.Busy,
    };

    /// <summary>度量来源逐字可见：<c>builder</c> = 本进程实测，<c>job-discovery</c> = 本进程没跑构建时的兜底。</summary>
    private static BuildWaitOutcome Describe(
        SupplyJobStatus status,
        RecordingIndexBuilder recorder,
        int exitCode,
        string? note)
    {
        if (recorder.LastResult is { } result)
        {
            return new BuildWaitOutcome(
                exitCode, status, note, result.IndexedFileCount, result.TotalBytes, result.ElapsedMs, "builder");
        }

        return new BuildWaitOutcome(
            exitCode,
            status,
            note,
            status.DiscoveredFileCount,
            status.DiscoveredBytes,
            status.FinishedAt is { } finished ? (long)(finished - status.StartedAt).TotalMilliseconds : null,
            "job-discovery");
    }

    // ── cancel：跨进程必须如实失败 ─────────────────────────────────────────

    private static async Task<int> RunCancelAsync(SupplyCliOptions cli, SupplyCliHost host, TextWriter stdout)
    {
        var (indexOptions, _, _) = ResolveIndexOptions(cli);
        var composition = Compose(host, indexOptions);
        var coordinator = host.CreateCoordinator(composition);

        var jobId = cli.JobId!;
        var status = await coordinator.GetStatusAsync(jobId).ConfigureAwait(false);

        if (status is null)
        {
            // 本 CLI 每次调用都新建协调器 ⇒ 进程内台账必然为空 ⇒ 外来的 jobId 必然走到这里。
            // 这不代表"该 job 不存在"，只代表"本进程无法处理它"：如实报告 + 非零退出，绝不静默成功。
            var missing = new CancelJson(
                Command: "cancel",
                JobId: jobId,
                Lookup: NotInThisProcess,
                Detail: NotInThisProcessDetail,
                State: null,
                ExitCode: SupplyCliExitCodes.Busy);

            SupplyCliViews.RenderCancel(stdout, cli.Json, missing);
            return missing.ExitCode;
        }

        var cancelled = await coordinator.CancelAsync(jobId).ConfigureAwait(false);
        var dto = new CancelJson(
            Command: "cancel",
            JobId: jobId,
            Lookup: "in_this_process",
            Detail: cancelled
                ? "已请求取消；job 的取消令牌已触发（终态见 state）。"
                : "该 job 已处于终态或状态机拒绝本次流转，未发生取消。",
            State: status.State.ToString(),
            ExitCode: cancelled ? SupplyCliExitCodes.Success : SupplyCliExitCodes.Busy);

        SupplyCliViews.RenderCancel(stdout, cli.Json, dto);
        return dto.ExitCode;
    }

    // ── 组装 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 索引根解析：<c>plan</c>/<c>status</c> 允许省略（按引擎默认解析并**如实打印**）；
    /// <c>build</c> 的显式要求已在解析期强制（缺失 ⇒ 用法错误 5，绝不落到生产索引根）。
    /// </summary>
    private static (FullTextIndexOptions Options, string IndexRoot, bool Explicit) ResolveIndexOptions(SupplyCliOptions cli)
    {
        var indexRoot = cli.IndexRoot is { Length: > 0 } explicitRoot
            ? Path.GetFullPath(explicitRoot)
            : new FullTextIndexOptions().IndexRootDirectory;

        return (new FullTextIndexOptions { IndexRootDirectory = indexRoot }, indexRoot, cli.IndexRoot is not null);
    }

    private static SupplyCliComposition Compose(SupplyCliHost host, FullTextIndexOptions indexOptions)
    {
        var engine = host.CreateEngine(indexOptions);

        return new SupplyCliComposition(
            Engine: engine,
            Inventory: host.CreateInventory(indexOptions),
            Recorder: new RecordingIndexBuilder(host.CreateBuilder(engine)),
            Lease: host.CreateLease(indexOptions),
            CoordinatorOptions: host.CreateCoordinatorOptions());
    }

    // ── 磁盘观察（只读） ───────────────────────────────────────────────────

    /// <summary>索引根本身只看顶层条目数（生产根可能很大，不做递归，避免无谓 IO）。</summary>
    private static (bool Exists, int TopLevelEntries) ObserveTopLevel(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? (true, Directory.GetDirectories(directory).Length)
                : (false, 0);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return (Directory.Exists(directory), 0);
        }
    }

    /// <summary>递归统计目录条目数（文件 + 子目录）与字节数；目录不可读时局部化，不中断整次观察。</summary>
    private static (bool Exists, int EntryCount, long Bytes) ObserveDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return (false, 0, 0);

        var entries = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] files;
            string[] subDirectories;
            try
            {
                files = Directory.GetFiles(current);
                subDirectories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                entries++;
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // 单文件不可读：条目数照记，字节数不猜。
                }
            }

            foreach (var subDirectory in subDirectories)
            {
                entries++;
                pending.Push(subDirectory);
            }
        }

        return (true, entries, bytes);
    }
}
