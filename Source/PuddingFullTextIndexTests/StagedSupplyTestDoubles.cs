using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A2a 测试替身与工具：可控的 **rooted 引擎**（能按需写字节 / 记录调用与顺序）、
/// 可注入失败的 **目录移动原语**、以及「目录树快照」这类机械断言工具。
/// <para>
/// ⚠️ 硬护栏与 A1 一致：所有用例只在 <see cref="Path.GetTempPath"/> 下工作（见 <see cref="TempSupplyFixture"/>），
/// 绝不触碰真实索引根 <c>D:\data\fulltext-index</c>。
/// </para>
/// </summary>
internal sealed class FakeRootedEngine : IFullTextIndexRootedEngine
{
    private readonly List<string> _events;
    private int _buildCalls;

    internal FakeRootedEngine(string indexRoot, List<string>? events = null)
    {
        IndexRoot = indexRoot;
        _events = events ?? new List<string>();
    }

    /// <summary>本实例绑定的索引根（staging 实例为 staging 根）。</summary>
    internal string IndexRoot { get; }

    internal int BuildCallCount => Volatile.Read(ref _buildCalls);

    internal List<string> BuildRequests { get; } = new();

    internal List<string> InvalidateRequests { get; } = new();

    /// <summary>构建时在索引目录里写入的字节数（模拟真实索引体积）；null = 不写。</summary>
    internal int? WriteBytesOnBuild { get; set; }

    /// <summary>覆盖构建行为（用于制造引擎失败）；返回 null 表示走默认行为。</summary>
    internal Func<string, CancellationToken, Task<FullTextIndexResult>>? BuildBehaviour { get; set; }

    /// <summary>
    /// 文档数探针上报的篇数（A22a）：<c>null</c> = 未显式设置 ⇒ 索引目录存在时报 <c>1</c> 篇（模拟健康索引）。
    /// 要制造回归场景就显式设值（例如 live=100 / staging=0）。
    /// </summary>
    internal long? DocumentsOnProbe { get; set; }

    /// <summary>整体覆盖探针返回值（含 <c>Exists</c> 与 <c>Documents=null</c> 语义）；设置后优先于 <see cref="DocumentsOnProbe"/>。</summary>
    internal IndexDocumentProbe? ProbeOverride { get; set; }

    /// <summary>探针被调用的语料根（断言「闸门确实探过 staging」）。</summary>
    internal List<string> ProbeRequests { get; } = new();

    internal FullTextIndexResult DefaultResult { get; set; } = new(true, 1, 128, 3, null);

    public string ResolveIndexDirectory(string corpusRootPath) => Path.Combine(IndexRoot, IndexDirectoryName(corpusRootPath));

    public void InvalidateScope(string corpusRootPath)
    {
        InvalidateRequests.Add(corpusRootPath);
        _events.Add($"invalidate:{corpusRootPath}");
    }

    public bool HasIndex(string directoryPath) => Directory.Exists(ResolveIndexDirectory(directoryPath));

    /// <summary>
    /// 文档数探针替身（A22a R1）：<c>Exists</c> 一律按目录实际是否存在（与真实引擎的三态语义同形），
    /// <c>Documents</c> 取 <see cref="DocumentsOnProbe"/>（未设时为 1；要模拟「读不出」请用 <see cref="ProbeOverride"/>）。
    /// </summary>
    public IndexDocumentProbe ProbeDocuments(string corpusRootPath)
    {
        ProbeRequests.Add(corpusRootPath);

        if (ProbeOverride is { } overridden)
            return overridden;

        var indexDirectory = ResolveIndexDirectory(corpusRootPath);
        return Directory.Exists(indexDirectory)
            ? new IndexDocumentProbe(Exists: true, DocumentsOnProbe ?? 1)
            : new IndexDocumentProbe(Exists: false, Documents: null);
    }

    public Task<FullTextSearchResult> SearchAsync(
        string query,
        string directoryPath,
        int maxResults = 30,
        string? fileExtensionFilter = null,
        string? subDirectoryFilter = null,
        CancellationToken ct = default,
        FullTextSearchScope? scope = null) =>
        Task.FromResult(new FullTextSearchResult(true, Array.Empty<FullTextSearchMatch>(), null, 0, 0));

    public async Task<FullTextIndexResult> BuildIndexAsync(
        string directoryPath, string? filePatterns = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _buildCalls);
        BuildRequests.Add(directoryPath);
        _events.Add($"build:{directoryPath}");

        if (BuildBehaviour is { } behaviour)
            return await behaviour(directoryPath, ct).ConfigureAwait(false);

        var indexDirectory = ResolveIndexDirectory(directoryPath);
        Directory.CreateDirectory(indexDirectory);

        if (WriteBytesOnBuild is { } bytes && bytes > 0)
            File.WriteAllBytes(Path.Combine(indexDirectory, "segments_1"), new byte[bytes]);

        return DefaultResult;
    }

    public bool RemoveIndex(string directoryPath) => false;

    /// <summary>索引目录名：与真实引擎同形（大写规范化全路径的 sha256 小写 hex）——保证被 live 用量口径识别为 live 目录。</summary>
    internal static string IndexDirectoryName(string corpusRootPath) =>
        SupplyIndexDirectoryLayout.Sha256Hex(
            Path.GetFullPath(corpusRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant());
}

/// <summary>
/// 目录移动原语替身：记录每次移动与**统一的顺序事件**，可按调用序号注入失败（A4 回滚断言的前提），
/// 并可在某次移动**成功之后**执行回调（用于制造「移动成功但后续清理失败」这类确定性故障）。
/// </summary>
internal sealed class FakeIndexDirectorySwapper : IIndexDirectorySwapper
{
    private readonly List<string> _events;
    private readonly Func<string, string, int, Exception?>? _failureFactory;
    private readonly Action<string, string, int>? _afterMove;
    private int _calls;

    internal FakeIndexDirectorySwapper(
        List<string>? events = null,
        Func<string, string, int, Exception?>? failureFactory = null,
        Action<string, string, int>? afterMove = null)
    {
        _events = events ?? new List<string>();
        _failureFactory = failureFactory;
        _afterMove = afterMove;
    }

    /// <summary>（源, 目标, 是否失败）。</summary>
    internal List<(string Source, string Destination, bool Failed)> Moves { get; } = new();

    public void Move(string sourceDirectory, string destinationDirectory)
    {
        var index = Interlocked.Increment(ref _calls);
        _events.Add($"move:{sourceDirectory}->{destinationDirectory}");

        var failure = _failureFactory?.Invoke(sourceDirectory, destinationDirectory, index);
        if (failure is not null)
        {
            Moves.Add((sourceDirectory, destinationDirectory, true));
            throw failure;
        }

        Directory.Move(sourceDirectory, destinationDirectory);
        Moves.Add((sourceDirectory, destinationDirectory, false));
        _afterMove?.Invoke(sourceDirectory, destinationDirectory, index);
    }
}

internal static class StagedSupplyTestHelpers
{
    /// <summary>造一个带 job 载荷的 scope（默认 jobId 固定，便于推算 staging 路径）。</summary>
    internal static SupplyScope ScopeFor(
        this TempSupplyFixture fixture,
        long? budgetBytes = null,
        long? corpusBytes = null,
        string jobId = "job-test-1") =>
        new(fixture.ScopeKey, fixture.Corpus, budgetBytes, corpusBytes, jobId);

    /// <summary>本次 job 的 staging 根（测试用推算；与实现同布局）。</summary>
    internal static string StagingRootFor(this TempSupplyFixture fixture, SupplyScope scope) =>
        SupplyIndexDirectoryLayout.ResolveStagingRoot(fixture.IndexRoot, scope.ScopeKey, scope.JobId!);

    internal static string StagingRoot(this TempSupplyFixture fixture) =>
        SupplyIndexDirectoryLayout.StagingRoot(fixture.IndexRoot);

    internal static string TrashRoot(this TempSupplyFixture fixture) =>
        SupplyIndexDirectoryLayout.TrashRoot(fixture.IndexRoot);

    /// <summary>在 .staging/.trash 下放一个「假残留」目录，并把最后写入时间设成 <paramref name="ageHours"/> 小时前。</summary>
    internal static string PlantResidue(this TempSupplyFixture fixture, string relativePath, double ageHours)
    {
        var path = Path.Combine(fixture.IndexRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "stale.bin"), "stale");
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
        return path;
    }

    /// <summary>目录树快照（相对路径 + 长度，排序）；目录不存在 ⇒ <c>&lt;missing&gt;</c>。</summary>
    internal static string SnapshotTree(string directory)
    {
        if (!Directory.Exists(directory))
            return "<missing>";

        var entries = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.GetFiles(current))
                entries.Add($"{Path.GetRelativePath(directory, file)}|{new FileInfo(file).Length}");

            foreach (var sub in Directory.GetDirectories(current))
            {
                entries.Add($"{Path.GetRelativePath(directory, sub)}/");
                pending.Push(sub);
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return string.Join("\n", entries);
    }

    /// <summary>把若干文件钉住（FileShare.None），用于制造「删除失败」这类确定性故障。</summary>
    internal static List<FileStream> LockFiles(params string[] filePaths) =>
        filePaths.Select(p => new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)).ToList();
}

/// <summary>
/// A2a 单元测试夹具：临时索引根 + fake live 引擎 + 按 job 造 fake staging 引擎的工厂 + fake 移动原语
/// + 统一顺序事件列表（用于断言 R3 的步骤顺序与 R4 的失效时机）。
/// </summary>
internal sealed class StagedRig : IDisposable
{
    internal StagedRig(
        long budgetBytes = 1_000_000,
        int? stagingWriteBytes = 4096,
        bool useStaging = true,
        TimeSpan? staleArtifactMaxAge = null,
        Func<FakeRootedEngine, FakeRootedEngine>? stagingCustomizer = null,
        Func<List<string>, IIndexDirectorySwapper>? swapperFactory = null,
        double? minStagingToLiveDocRatio = null)
    {
        Fixture = new TempSupplyFixture();
        Events = new List<string>();
        Swapper = swapperFactory?.Invoke(Events) ?? new FakeIndexDirectorySwapper(Events);
        LiveEngine = new FakeRootedEngine(Fixture.IndexRoot, Events);

        Options = new SupplyCoordinatorOptions
        {
            DefaultBudgetBytes = budgetBytes,
            UseStaging = useStaging,
            StaleArtifactMaxAge = staleArtifactMaxAge ?? TimeSpan.FromHours(24),
            MinStagingToLiveDocRatio = minStagingToLiveDocRatio ?? SupplyCoordinatorOptions.DefaultMinStagingToLiveDocRatio,
        };

        Builder = new StagedFullTextIndexBuilder(
            LiveEngine,
            stagingOptions =>
            {
                var engine = new FakeRootedEngine(stagingOptions.IndexRootDirectory, Events)
                {
                    WriteBytesOnBuild = stagingWriteBytes,
                };

                var customised = stagingCustomizer?.Invoke(engine) ?? engine;
                StagingEngines.Add(customised);
                return customised;
            },
            Fixture.Options,
            Options,
            Swapper);
    }

    internal TempSupplyFixture Fixture { get; }

    internal SupplyCoordinatorOptions Options { get; }

    internal FakeRootedEngine LiveEngine { get; }

    internal List<FakeRootedEngine> StagingEngines { get; } = new();

    internal IIndexDirectorySwapper Swapper { get; }

    internal List<string> Events { get; }

    internal StagedFullTextIndexBuilder Builder { get; }

    /// <summary>live 引擎为某语料根解析出的索引目录（= 真实布局里的 live 目录）。</summary>
    internal string LiveDirectory => LiveEngine.ResolveIndexDirectory(Fixture.Corpus);

    /// <summary>本次 job 的 staging 根（按 scope 载荷里的 jobId 推算）。</summary>
    internal string StagingRootFor(SupplyScope scope) => Fixture.StagingRootFor(scope);

    public void Dispose() => Fixture.Dispose();
}
