using System.Text.Json;
using PuddingFullTextIndex.Cli;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndex.Cli.Tests;

/// <summary>
/// CLI 测试夹具。
/// <para>
/// ⚠️ 硬护栏（照 A1 的做法）：所有用例**只能**在 <see cref="Path.GetTempPath"/> 下工作，
/// 构造函数直接断言；索引根本身<b>不</b>预创建（<c>plan</c> 的零写入断言需要它从"不存在"开始）。
/// </para>
/// </summary>
internal sealed class CliFixture : IDisposable
{
    internal CliFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "pudding-fts-cli-" + Guid.NewGuid().ToString("N"));
        Corpus = Path.Combine(Root, "corpus");
        OtherCorpus = Path.Combine(Root, "corpus-two");
        IndexRoot = Path.Combine(Root, "index");
        Directory.CreateDirectory(Corpus);
        Directory.CreateDirectory(OtherCorpus);

        if (!Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"CLI 测试只允许使用系统临时目录，实际为 {Root}");

        if (Root.Contains(@"D:\data", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"CLI 测试绝不允许指向真实数据根：{Root}");
    }

    internal string Root { get; }

    /// <summary>主语料目录（已创建；作为 <c>--scope</c>）。</summary>
    internal string Corpus { get; }

    /// <summary>第二个不重叠语料目录（用于多 scope / 嵌套拒绝用例）。</summary>
    internal string OtherCorpus { get; }

    /// <summary>临时索引根（**必须**由用例显式传给 <c>--index-root</c>）。</summary>
    internal string IndexRoot { get; }

    /// <summary>在主语料下写文件，返回绝对路径。</summary>
    internal string Write(string relativePath, string content) => WriteTo(Corpus, relativePath, content);

    internal string WriteTo(string corpusRoot, string relativePath, string content)
    {
        var path = Path.Combine(corpusRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>在主语料下建子目录（用于构造父子嵌套 scope）。</summary>
    internal string CreateSubDirectory(string relativePath)
    {
        var path = Path.Combine(Corpus, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    internal string MissingDirectory => Path.Combine(Root, "does-not-exist");

    /// <summary>跑一次 CLI（默认替身装配）。</summary>
    internal CliRun Run(params string[] args) => RunWith(new CliTestHost(), args);

    internal CliRun RunWith(CliTestHost host, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = SupplyCli.RunAsync(args, stdout, stderr, host.Build()).GetAwaiter().GetResult();
        return new CliRun(stdout.ToString(), stderr.ToString(), exitCode);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应把测试判红
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>递归快照目录内的全部条目（文件 + 目录；目录不存在时为空）。</summary>
    internal static IReadOnlyList<string> CaptureEntries(string directory)
    {
        var entries = new List<string>();
        if (!Directory.Exists(directory))
            return entries;

        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var file in Directory.GetFiles(current))
                entries.Add(Path.GetRelativePath(directory, file));

            foreach (var sub in Directory.GetDirectories(current))
            {
                entries.Add(Path.GetRelativePath(directory, sub) + Path.DirectorySeparatorChar);
                pending.Push(sub);
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }
}

/// <summary>一次 CLI 调用的原始结果（标准输出 / 标准错误 / 退出码）。</summary>
internal sealed record CliRun(string StdOut, string StdErr, int ExitCode)
{
    /// <summary>取人类可读行 <c>key = value</c> 的值（键取规格里的字段名）。</summary>
    internal string Value(string key)
    {
        var prefix = key + " = ";
        foreach (var line in StdOut.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return trimmed[prefix.Length..];
        }

        throw new InvalidOperationException($"输出里没有 '{key}' 行。完整输出：\n{StdOut}");
    }

    internal int IntValue(string key) => int.Parse(Value(key), System.Globalization.CultureInfo.InvariantCulture);

    internal long LongValue(string key) => long.Parse(Value(key), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>把标准输出当 JSON 解析（形状断言用）。</summary>
    internal JsonDocument Json()
    {
        try
        {
            return JsonDocument.Parse(StdOut);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"标准输出不是合法 JSON：{ex.Message}\n{StdOut}");
        }
    }
}

/// <summary>CLI 组合根的测试装配：计数器 + 可注入替身（builder / lease / coordinator）。</summary>
internal sealed class CliTestHost
{
    /// <summary>BuilderFactory 实际造出的替身构建器（用于断言"没有执行构建"）。</summary>
    internal StubIndexBuilder? SuppliedBuilder { get; private set; }

    internal int EngineFactoryCalls { get; private set; }

    internal int InventoryFactoryCalls { get; private set; }

    internal int BuilderFactoryCalls { get; private set; }

    internal int LeaseFactoryCalls { get; private set; }

    /// <summary>构建行为；null = 用真实 <see cref="FullTextSearchEngineIndexBuilder"/>（真 Lucene）。</summary>
    internal Func<SupplyScope, CancellationToken, Task<SupplyBuildResult>>? BuildBehaviour { get; set; }

    internal IFullTextSupplyLease? LeaseOverride { get; set; }

    internal IFullTextIndexSupplyCoordinator? CoordinatorOverride { get; set; }

    internal TimeSpan? BuildTimeout { get; set; }

    internal SupplyCliHost Build() => new(
        EngineFactory: options =>
        {
            EngineFactoryCalls++;
            return new LuceneSearchEngine(options);
        },
        InventoryFactory: options =>
        {
            InventoryFactoryCalls++;
            return new FileSystemSupplyInventory(options);
        },
        BuilderFactory: engine =>
        {
            BuilderFactoryCalls++;
            if (BuildBehaviour is null)
                return new FullTextSearchEngineIndexBuilder(engine);

            SuppliedBuilder = new StubIndexBuilder(BuildBehaviour);
            return SuppliedBuilder;
        },
        LeaseFactory: options =>
        {
            LeaseFactoryCalls++;
            return LeaseOverride ?? new FileSupplyLease(options);
        },
        CoordinatorOptionsFactory: () => new SupplyCoordinatorOptions(),
        CoordinatorFactory: composition => CoordinatorOverride
            ?? new FullTextIndexSupplyCoordinator(
                composition.Inventory, composition.Recorder, composition.Lease, composition.CoordinatorOptions),
        // 轮询/等待在测试里压到最小：真正的超时由 BuildTimeout=Zero 表达。
        DelayAsync: (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(5), ct),
        BuildTimeout: BuildTimeout);
}

/// <summary>构建端口替身：记录调用次数，行为可注入（失败 / 抛异常 / 成功）。</summary>
internal sealed class StubIndexBuilder : IFullTextIndexBuilder
{
    private readonly Func<SupplyScope, CancellationToken, Task<SupplyBuildResult>> _behaviour;
    private int _calls;

    internal StubIndexBuilder(Func<SupplyScope, CancellationToken, Task<SupplyBuildResult>> behaviour)
    {
        ArgumentNullException.ThrowIfNull(behaviour);
        _behaviour = behaviour;
    }

    internal int BuildCallCount => Volatile.Read(ref _calls);

    public Task<SupplyBuildResult> BuildAsync(SupplyScope scope, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        return _behaviour(scope, ct);
    }

    internal static SupplyBuildResult Failed(string error) => new(false, 0, 0, 5, error);
}

/// <summary>租约替身：永远被"别的进程"占用（构造 <c>Busy</c> 路径）。</summary>
internal sealed class AlwaysBusyLease : IFullTextSupplyLease
{
    internal SupplyLeaseHolder Holder { get; } = new(
        OwnerId: "other-machine#4242",
        ProcessId: 4242,
        MachineName: "other-machine",
        StartedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
        HeartbeatUtc: DateTimeOffset.UtcNow,
        JobId: "other-job",
        IsExpired: false,
        TakeoverReason: null);

    internal int AcquireCalls { get; private set; }

    public Task<SupplyLeaseAcquireResult> TryAcquireAsync(
        string scopeKey,
        SupplyLeaseOwner owner,
        string? jobId = null,
        CancellationToken ct = default)
    {
        AcquireCalls++;
        return Task.FromResult(new SupplyLeaseAcquireResult(
            Acquired: false,
            Lease: null,
            Holder: Holder,
            Message: "替身：租约被 other-machine#4242 持有。"));
    }

    public Task<bool> RenewAsync(string scopeKey, string ownerId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> ReleaseAsync(string scopeKey, string ownerId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<SupplyLeaseHolder?> DescribeHolderAsync(string scopeKey, CancellationToken ct = default) =>
        Task.FromResult<SupplyLeaseHolder?>(Holder);
}

/// <summary>
/// 协调器替身：只覆盖 <c>status</c> / <c>cancel</c> / <c>build --wait</c> 需要的读路径。
/// 本进程 job 台账可预置 ⇒ 能覆盖"本进程命中"与"跨进程"两条分支。
/// </summary>
internal sealed class FakeCoordinator : IFullTextIndexSupplyCoordinator
{
    private readonly Dictionary<string, SupplyJobStatus> _jobs = new(StringComparer.Ordinal);

    internal SupplyRequestOutcome NextBuildOutcome { get; set; } =
        new(SupplyOutcome.Merged, "fake-job", "替身：合并到既有 job。", null, Array.Empty<SupplyScopeOutcome>());

    internal bool CancelSucceeds { get; set; } = true;

    internal int CancelCalls { get; private set; }

    internal int BuildCalls { get; private set; }

    internal void AddJob(SupplyJobStatus status) => _jobs[status.JobId] = status;

    /// <summary>构造一条 job 状态（scopeKey 用 CLI 镜像口径，保证与观测一致）。</summary>
    internal static SupplyJobStatus Job(
        string jobId,
        SupplyJobState state,
        string phase,
        string rootPath,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt,
        int discoveredFileCount,
        long discoveredBytes,
        string? message) =>
        new(
            jobId,
            SupplyScopeMirror.ToScopeKey(SupplyScopeMirror.NormalizeRoot(rootPath)),
            rootPath,
            state,
            phase,
            discoveredFileCount,
            discoveredBytes,
            startedAt,
            finishedAt,
            message,
            LeaseHolder: null);

    public Task<SupplyPlanResult> PlanAsync(SupplyScopeRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException("替身不提供 plan 路径。");

    public Task<SupplyRequestOutcome> BuildAsync(SupplyScopeRequest request, CancellationToken ct = default)
    {
        BuildCalls++;
        return Task.FromResult(NextBuildOutcome);
    }

    public Task<SupplyJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default) =>
        Task.FromResult(_jobs.TryGetValue(jobId, out var status) ? status : null);

    public Task<IReadOnlyList<SupplyJobStatus>> ListStatusAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SupplyJobStatus>>(
            _jobs.Values.OrderBy(j => j.StartedAt).ThenBy(j => j.JobId, StringComparer.Ordinal).ToList());

    public Task<bool> CancelAsync(string jobId, CancellationToken ct = default)
    {
        CancelCalls++;
        return Task.FromResult(CancelSucceeds);
    }
}
