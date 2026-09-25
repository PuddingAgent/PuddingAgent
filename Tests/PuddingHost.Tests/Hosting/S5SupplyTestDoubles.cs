using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// S5 测试夹具：**只**在 <see cref="Path.GetTempPath"/> 下工作（构造函数直接断言），
/// 索引根**不预创建**（「默认关闭 ⇒ 索引根零写入」需要它从「不存在」开始）。
/// </summary>
internal sealed class S5Fixture : IDisposable
{
    internal S5Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "pudding-s5-host", Guid.NewGuid().ToString("N"));
        IndexRoot = Path.Combine(Root, "index");
        Directory.CreateDirectory(Root);

        if (!Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"S5 测试只允许使用系统临时目录，实际为 {Root}");

        Options = new FullTextIndexOptions { IndexRootDirectory = IndexRoot };
    }

    /// <summary>夹具根（临时）。</summary>
    internal string Root { get; }

    /// <summary>索引根（**不预创建**）。</summary>
    internal string IndexRoot { get; }

    /// <summary>索引选项（索引根指向 <see cref="IndexRoot"/>）。</summary>
    internal FullTextIndexOptions Options { get; }

    /// <summary>建一个语料目录（含给定文件），返回其绝对路径。</summary>
    internal string NewCorpus(string name, params (string RelativePath, string Content)[] files)
    {
        var corpus = Path.Combine(Root, "corpus", name);
        Directory.CreateDirectory(corpus);

        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(corpus, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return corpus;
    }

    /// <summary>
    /// 在 <paramref name="parent"/> 下造一个「看起来像 live 索引目录」的目录，并把**它自己**的
    /// mtime 钉到 <paramref name="lastWriteUtc"/>。
    /// <para>
    /// ⚠️ 改子目录时间戳**不会**改父目录 mtime —— M3（per-scope 新鲜度退回索引根 mtime）正是靠这一点取红：
    /// 退回根口径后「陈旧」的那个 scope 也会被当成新鲜。
    /// </para>
    /// </summary>
    internal static string CreateIndexDirectory(string parent, string name, DateTime lastWriteUtc)
    {
        var directory = Path.Combine(parent, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "segments_1"), "fake-index");
        Directory.SetLastWriteTimeUtc(directory, lastWriteUtc);
        return directory;
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
            // 临时目录清理失败不应把测试判红。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 记录型协调器装饰器：把调用**转发**给内层（生产/真实）协调器，同时记录提交、轮询与终态，
/// 让宿主侧行为（「只提交一次」「轮询到终态」）变成可断言的事实。
/// </summary>
internal sealed class RecordingSupplyCoordinator : IFullTextIndexSupplyCoordinator
{
    private readonly IFullTextIndexSupplyCoordinator _inner;
    private readonly ConcurrentQueue<SupplyScopeRequest> _buildRequests = new();
    private readonly ConcurrentQueue<SupplyJobStatus> _terminal = new();

    internal RecordingSupplyCoordinator(IFullTextIndexSupplyCoordinator inner) => _inner = inner;

    /// <summary>收到的提交请求（按顺序）。</summary>
    internal IReadOnlyList<SupplyScopeRequest> BuildRequests => _buildRequests.ToArray();

    /// <summary>被提交的 scope 路径（按顺序）——用于断言「只提交了该提交的那些」。</summary>
    internal IReadOnlyList<string> SubmittedRootPaths =>
        _buildRequests.Select(static r => r.RootPaths[0]).ToArray();

    /// <summary>观测到的终态快照（按顺序）。</summary>
    internal IReadOnlyList<SupplyJobStatus> TerminalStates => _terminal.ToArray();

    /// <summary>首个终态（供「等构建真的结束」用；不轮询、无忙等）。</summary>
    internal TaskCompletionSource<SupplyJobStatus> FirstTerminal { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int GetStatusCalls;

    public Task<SupplyPlanResult> PlanAsync(SupplyScopeRequest request, CancellationToken ct = default) =>
        _inner.PlanAsync(request, ct);

    public Task<SupplyRequestOutcome> BuildAsync(SupplyScopeRequest request, CancellationToken ct = default)
    {
        _buildRequests.Enqueue(request);
        return _inner.BuildAsync(request, ct);
    }

    public async Task<SupplyJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref GetStatusCalls);
        var status = await _inner.GetStatusAsync(jobId, ct).ConfigureAwait(false);

        if (status is not null && IsTerminal(status.State))
        {
            _terminal.Enqueue(status);
            FirstTerminal.TrySetResult(status);
        }

        return status;
    }

    public Task<IReadOnlyList<SupplyJobStatus>> ListStatusAsync(CancellationToken ct = default) =>
        _inner.ListStatusAsync(ct);

    public Task<bool> CancelAsync(string jobId, CancellationToken ct = default) =>
        _inner.CancelAsync(jobId, ct);

    internal static bool IsTerminal(SupplyJobState state) =>
        state is SupplyJobState.Succeeded or SupplyJobState.Failed or SupplyJobState.Cancelled;
}

/// <summary>
/// 脚本化协调器：**零副作用、全计数** —— 用于「默认关闭 ⇒ 协调器 0 次调用」与
/// 「轮询超时必须有界」这两类断言（这两类都要求协调器**不碰真实磁盘**）。
/// </summary>
internal sealed class ScriptedSupplyCoordinator : IFullTextIndexSupplyCoordinator
{
    private readonly Func<SupplyScopeRequest, int, SupplyRequestOutcome> _build;
    private readonly Func<string, int, SupplyJobStatus?> _status;

    internal ScriptedSupplyCoordinator(
        Func<SupplyScopeRequest, int, SupplyRequestOutcome>? buildBehaviour = null,
        Func<string, int, SupplyJobStatus?>? statusBehaviour = null)
    {
        _build = buildBehaviour ?? ((request, _) => Started(request.RootPaths[0], "job-1"));
        _status = statusBehaviour ?? ((jobId, _) => Succeeded(jobId));
    }

    internal int BuildCalls;

    internal int GetStatusCalls;

    internal int PlanCalls;

    internal int ListStatusCalls;

    internal int CancelCalls;

    internal ConcurrentQueue<string> SubmittedRootPaths { get; } = new();

    public Task<SupplyPlanResult> PlanAsync(SupplyScopeRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref PlanCalls);
        return Task.FromResult(new SupplyPlanResult(false, [], [], 0, 0));
    }

    public Task<SupplyRequestOutcome> BuildAsync(SupplyScopeRequest request, CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref BuildCalls);
        foreach (var path in request.RootPaths)
            SubmittedRootPaths.Enqueue(path);

        return Task.FromResult(_build(request, call));
    }

    public Task<SupplyJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref GetStatusCalls);
        return Task.FromResult(_status(jobId, call));
    }

    public Task<IReadOnlyList<SupplyJobStatus>> ListStatusAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref ListStatusCalls);
        return Task.FromResult<IReadOnlyList<SupplyJobStatus>>([]);
    }

    public Task<bool> CancelAsync(string jobId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CancelCalls);
        return Task.FromResult(false);
    }

    internal static SupplyRequestOutcome Started(string rootPath, string jobId) => new(
        SupplyOutcome.Started,
        jobId,
        "started",
        null,
        [new SupplyScopeOutcome(rootPath, rootPath, SupplyOutcome.Started, jobId, "started", null)]);

    internal static SupplyRequestOutcome Busy(string rootPath, SupplyLeaseHolder? holder = null) => new(
        SupplyOutcome.Busy,
        null,
        "busy",
        holder,
        [new SupplyScopeOutcome(rootPath, rootPath, SupplyOutcome.Busy, null, "busy", holder)]);

    internal static SupplyJobStatus Running(string jobId) => new(
        jobId,
        "scope-key",
        "scope-root",
        SupplyJobState.Running,
        SupplyJobPhases.Building,
        3,
        4096,
        DateTimeOffset.UtcNow,
        null,
        "building…",
        null);

    internal static SupplyJobStatus Succeeded(string jobId) => new(
        jobId,
        "scope-key",
        "scope-root",
        SupplyJobState.Succeeded,
        SupplyJobPhases.Completed,
        3,
        4096,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "done",
        null);
}

/// <summary>
/// 计数型 rooted 引擎：语料根 → <c>&lt;fakeIndexRoot&gt;/&lt;目录名&gt;</c> 的**确定性**映射，
/// 只计数、不建真索引。用于把「宿主到底碰了什么」变成可断言的计数
/// （<c>HasIndex</c> / <c>ResolveIndexDirectory</c> / <c>BuildIndexAsync</c>）。
/// </summary>
internal sealed class CountingRootedEngine : IFullTextIndexRootedEngine
{
    private readonly string _fakeIndexRoot;

    internal CountingRootedEngine(string fakeIndexRoot) => _fakeIndexRoot = fakeIndexRoot;

    internal int HasIndexCalls;

    internal int BuildCalls;

    internal int ResolveCalls;

    internal int InvalidateCalls;

    internal int SearchCalls;

    internal ConcurrentQueue<string> ResolvedScopes { get; } = new();

    internal ConcurrentQueue<string> BuiltScopes { get; } = new();

    /// <summary>语料根 → 假索引目录（测试自己的确定性映射；不复制引擎哈希规则）。</summary>
    internal string Map(string corpusRootPath) =>
        Path.Combine(_fakeIndexRoot, Path.GetFileName(Path.TrimEndingDirectorySeparator(corpusRootPath)));

    public bool HasIndex(string directoryPath)
    {
        Interlocked.Increment(ref HasIndexCalls);
        return Directory.Exists(Map(directoryPath));
    }

    public Task<FullTextSearchResult> SearchAsync(
        string query,
        string directoryPath,
        int maxResults = 30,
        string? fileExtensionFilter = null,
        string? subDirectoryFilter = null,
        CancellationToken ct = default,
        FullTextSearchScope? scope = null)
    {
        Interlocked.Increment(ref SearchCalls);
        return Task.FromResult(new FullTextSearchResult(false, [], "test double", 0, 0));
    }

    public Task<FullTextIndexResult> BuildIndexAsync(
        string directoryPath,
        string? filePatterns = null,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref BuildCalls);
        BuiltScopes.Enqueue(directoryPath);
        return Task.FromResult(new FullTextIndexResult(true, 1, 1, 1, null));
    }

    public bool RemoveIndex(string directoryPath) => false;

    public string ResolveIndexDirectory(string corpusRootPath)
    {
        Interlocked.Increment(ref ResolveCalls);
        ResolvedScopes.Enqueue(corpusRootPath);
        return Map(corpusRootPath);
    }

    public void InvalidateScope(string corpusRootPath) => Interlocked.Increment(ref InvalidateCalls);
}

/// <summary>
/// 间谍 rooted 引擎：把**真实** Lucene 引擎包起来，只为计数 ——
/// 「live 实例上到底有没有发生直写」（<see cref="BuildCalls"/> == 0）与
/// 「切换前后是否失效了 reader 缓存」这两条断言靠它成立。
/// </summary>
internal sealed class SpyRootedEngine : IFullTextIndexRootedEngine
{
    private readonly LuceneSearchEngine _inner;

    internal SpyRootedEngine(LuceneSearchEngine inner) => _inner = inner;

    internal int BuildCalls;

    internal int InvalidateCalls;

    internal int SearchCalls;

    internal ConcurrentQueue<string> BuiltScopes { get; } = new();

    /// <inheritdoc />
    public bool HasIndex(string directoryPath) => _inner.HasIndex(directoryPath);

    /// <inheritdoc />
    public Task<FullTextSearchResult> SearchAsync(
        string query,
        string directoryPath,
        int maxResults = 30,
        string? fileExtensionFilter = null,
        string? subDirectoryFilter = null,
        CancellationToken ct = default,
        FullTextSearchScope? scope = null)
    {
        Interlocked.Increment(ref SearchCalls);
        return _inner.SearchAsync(query, directoryPath, maxResults, fileExtensionFilter, subDirectoryFilter, ct, scope);
    }

    /// <inheritdoc />
    public Task<FullTextIndexResult> BuildIndexAsync(
        string directoryPath,
        string? filePatterns = null,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref BuildCalls);
        BuiltScopes.Enqueue(directoryPath);
        return _inner.BuildIndexAsync(directoryPath, filePatterns, ct);
    }

    /// <inheritdoc />
    public bool RemoveIndex(string directoryPath) => _inner.RemoveIndex(directoryPath);

    /// <inheritdoc />
    public string ResolveIndexDirectory(string corpusRootPath) =>
        ((IFullTextIndexRootedEngine)_inner).ResolveIndexDirectory(corpusRootPath);

    /// <inheritdoc />
    public void InvalidateScope(string corpusRootPath)
    {
        Interlocked.Increment(ref InvalidateCalls);
        ((IFullTextIndexRootedEngine)_inner).InvalidateScope(corpusRootPath);
    }
}

/// <summary>
/// 测试用供给组合：协调器（真实或脚本）+ 组件策略快照 + **可委托给生产实现**的新鲜度探针。
/// <para>
/// 把新鲜度探针委托给生产组合（<c>production.LiveIndexLastWriteUtc</c>）是关键：
/// 这样 A5 断言的仍是**生产代码**的 per-scope 口径，M3 变异（退回索引根 mtime）才能真正取红。
/// </para>
/// </summary>
internal sealed class TestSupplyComposition : IFullTextIndexSupplyComposition
{
    private readonly Func<string, DateTimeOffset> _liveIndexLastWriteUtc;

    internal TestSupplyComposition(
        IFullTextIndexSupplyCoordinator coordinator,
        SupplyCoordinatorOptions componentOptions,
        Func<string, DateTimeOffset> liveIndexLastWriteUtc)
    {
        Coordinator = coordinator;
        ComponentOptions = componentOptions;
        _liveIndexLastWriteUtc = liveIndexLastWriteUtc;
    }

    /// <inheritdoc />
    public IFullTextIndexSupplyCoordinator Coordinator { get; }

    /// <inheritdoc />
    public SupplyCoordinatorOptions ComponentOptions { get; }

    /// <inheritdoc />
    public DateTimeOffset LiveIndexLastWriteUtc(string scopeRootPath) => _liveIndexLastWriteUtc(scopeRootPath);
}

/// <summary>供给组合工厂替身：计数 + 记录入参（断言「默认关闭 ⇒ 连组合都不构造」）。</summary>
internal sealed class StubCompositionFactory : IFullTextIndexSupplyCompositionFactory
{
    private readonly Func<FullTextIndexSupplyOptions, IFullTextIndexSupplyComposition> _behaviour;

    internal StubCompositionFactory(IFullTextIndexSupplyComposition composition)
        : this(_ => composition)
    {
    }

    internal StubCompositionFactory(Func<FullTextIndexSupplyOptions, IFullTextIndexSupplyComposition> behaviour) =>
        _behaviour = behaviour;

    internal int CreateCalls;

    internal ConcurrentQueue<FullTextIndexSupplyOptions> CreatedWith { get; } = new();

    public IFullTextIndexSupplyComposition Create(FullTextIndexSupplyOptions supplyOptions)
    {
        Interlocked.Increment(ref CreateCalls);
        CreatedWith.Enqueue(supplyOptions);
        return _behaviour(supplyOptions);
    }
}

/// <summary>语料清点替身：只计数，不触盘。</summary>
internal sealed class CallCountingInventory : IFullTextIndexSupplyInventory
{
    internal int MeasureCalls;

    public Task<SupplyInventory> MeasureAsync(string rootPath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref MeasureCalls);
        return Task.FromResult(new SupplyInventory(
            1,
            32,
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [".txt"] = 32 }));
    }
}

/// <summary>索引构建替身：只计数（用于「哪个 scope 真的被构建了」）。</summary>
internal sealed class CallCountingBuilder : IFullTextIndexBuilder
{
    internal int BuildCalls;

    internal ConcurrentQueue<string> BuiltScopeKeys { get; } = new();

    public Task<SupplyBuildResult> BuildAsync(SupplyScope scope, CancellationToken ct = default)
    {
        Interlocked.Increment(ref BuildCalls);
        BuiltScopeKeys.Enqueue(scope.ScopeKey);
        return Task.FromResult(new SupplyBuildResult(true, 1, 32, 1, null));
    }
}

/// <summary>Logger 替身：按 <c>级别: 文本</c> 记下每条日志，供「必须可见 / 逐类如实」断言。</summary>
internal sealed class SupplyRecordingLogger : ILogger<PuddingAgent.Services.IndexPrebuildService>
{
    private readonly object _gate = new();
    private readonly List<string> _entries = [];

    internal IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToArray();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _entries.Add($"{logLevel}: {formatter(state, exception)}");
    }
}

/// <summary>S5 测试的公共工具。</summary>
internal static class S5TestHelpers
{
    /// <summary>有界等待（不忙等、不确定就失败）。</summary>
    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>递归快照目录内全部条目（相对路径；目录带尾分隔符），用于「逐字节不变」断言。</summary>
    internal static IReadOnlyList<string> SnapshotTree(string directory)
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

    /// <summary>路径比较（去尾分隔符 + 大小写不敏感）：同一目录的等价写法不算差异。</summary>
    internal static void AssertPathsEqual(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant();

        Xunit.Assert.Equal(
            expected.Select(Normalize).ToArray(),
            actual.Select(Normalize).ToArray());
    }
}
