using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;
using PuddingFullTextIndex.Infrastructure.Text;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndexTests;

/// <summary>
/// 可注入的**业务时钟**（S3d）：checkpoint 水位线 / 去抖期限 / 快照时刻全部走它。
/// <para>
/// 默认**冻结**在初始化值上：这样 M4「未变文件不被重复写」、M6「水位线不动」这类断言才是**确定性**的，
/// 而不是靠 sleep 撞时序。冻结时钟同时让「去抖 / 最大合并等待」永不自然到期 ⇒ 测试可以**显式**
/// 调用 <c>FlushPendingAsync</c> / <c>RunRecoveryScanAsync</c> 驱动，不受泵任务自动行为干扰。
/// </para>
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    internal FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    internal void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>可编排的资源压力探针（<c>null</c> = 不可采样 ⇒ 体检必须退避）。</summary>
internal sealed class FixedPressureProbe : IResourcePressureProbe
{
    internal ResourcePressureSample? Next { get; set; }

    public ResourcePressureSample? Sample() => Next;
}

/// <summary>
/// 可编排的提取器（<c>.pdf</c>）：按路径给出内容；命中 <see cref="FailingPaths"/> 抛 <see cref="IOException"/>。
/// <para>未编排内容的路径**掷地有声**（否则会静默返回空串、让用例假绿）。</para>
/// </summary>
internal sealed class ScriptedFactsExtractor : IFileContentExtractor
{
    private readonly Dictionary<string, string> _contents = new(StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> FailingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    internal void Set(string path, string content) => _contents[path] = content;

    public Task<string> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        if (FailingPaths.Contains(filePath))
            throw new IOException($"s3d-injected-extract-failure: {Path.GetFileName(filePath)}");

        if (_contents.TryGetValue(filePath, out var content))
            return Task.FromResult(content);

        throw new InvalidOperationException($"s3d-extractor: no scripted content for '{filePath}'");
    }
}

/// <summary>
/// 「卡在提取阶段」的提取器：进入时点亮 <see cref="Entered"/>，然后一直等到 <see cref="Release"/>（或取消）。
/// <para>用于把「一批正在飞行中」变成**确定性**输入（M11：StopAsync 期间到达的取消不得被吞）。</para>
/// </summary>
internal sealed class GatedExtractor : IFileContentExtractor
{
    /// <summary>是否「上膛」：未上膛时立即返回内容（供初始全量构建使用）。</summary>
    internal volatile bool Armed;

    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    public async Task<string> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        if (!Armed)
            return "gatedzzcontent";

        Entered.TrySetResult();

        // 取消必须能打断等待（否则 StopAsync 会一直挂住 —— 这正是 M11b 要证明的性质）。
        await Release.Task.WaitAsync(ct);
        return "gatedzzcontentnew";
    }
}

/// <summary>
/// 记录型执行层：**转发给真实引擎**（因此断言到的都是真实 Lucene 行为），并把每个变更集 / 结果 /
/// 体积报告留档，供「按路径集合」与「批量次数」这类断言取证。
/// <para>
/// ⚠️ 体积报告来自引擎的 internal <c>ApplyChangesWithReportAsync</c>（与 <c>ApplyChangesAsync</c> 同一实现），
/// 因此**无需**为了测试改变任何 public 面（M12 的出口正是它）。
/// </para>
/// </summary>
internal sealed class RecordingMaintenanceEngine : IFullTextIndexMaintenanceEngine
{
    private readonly LuceneFullTextIndexMaintenanceEngine _inner;
    private readonly object _lock = new();

    private readonly List<FullTextChangeSet> _changeSets = new();
    private readonly List<FullTextMutationResult> _results = new();
    private readonly List<IndexSizeReport> _reports = new();
    private int _probeCalls;

    internal RecordingMaintenanceEngine(LuceneFullTextIndexMaintenanceEngine inner) => _inner = inner;

    internal int BatchCount
    {
        get
        {
            lock (_lock)
            {
                return _changeSets.Count;
            }
        }
    }

    /// <summary>只读探针被调用的次数（体检退避时**不得**增加）。</summary>
    internal int ProbeCallCount
    {
        get
        {
            lock (_lock)
            {
                return _probeCalls;
            }
        }
    }

    internal IReadOnlyList<FullTextChangeSet> ChangeSets
    {
        get
        {
            lock (_lock)
            {
                return _changeSets.ToArray();
            }
        }
    }

    internal IReadOnlyList<FullTextMutationResult> Results
    {
        get
        {
            lock (_lock)
            {
                return _results.ToArray();
            }
        }
    }

    internal IReadOnlyList<IndexSizeReport> Reports
    {
        get
        {
            lock (_lock)
            {
                return _reports.ToArray();
            }
        }
    }

    /// <summary>最近一个「全部变更都来自给定来源」的批次；没有则返回 null。</summary>
    internal FullTextChangeSet? LastBatchWithSource(FullTextChangeSource source)
    {
        lock (_lock)
        {
            for (var i = _changeSets.Count - 1; i >= 0; i--)
            {
                var changeSet = _changeSets[i];
                if (changeSet.Changes.Count == 0)
                    continue;

                var all = true;
                foreach (var change in changeSet.Changes)
                {
                    if (change.Sources != source)
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                    return changeSet;
            }

            return null;
        }
    }

    public async Task<FullTextMutationResult> ApplyChangesAsync(
        FullTextChangeSet changeSet,
        FullTextMutationBudget budget,
        CancellationToken cancellationToken = default)
    {
        var (result, report) = await _inner.ApplyChangesWithReportAsync(changeSet, budget, cancellationToken);

        lock (_lock)
        {
            _changeSets.Add(changeSet);
            _results.Add(result);
            _reports.Add(report);
        }

        return result;
    }

    public IAsyncEnumerable<IndexedPathEntry> EnumerateIndexedPathsAsync(
        string scopeRoot,
        CancellationToken cancellationToken = default)
        => _inner.EnumerateIndexedPathsAsync(scopeRoot, cancellationToken);

    public Task<FullTextIndexIntegrityProbe> ProbeIntegrityAsync(
        string scopeRoot,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _probeCalls++;
        }

        return _inner.ProbeIntegrityAsync(scopeRoot, cancellationToken);
    }
}

/// <summary>
/// S3d 测试台：<c>%TEMP%</c> 下的语料根 + 索引根 + 维护器 + 记录型引擎 + 冻结时钟 + 可编排探针。
/// <list type="bullet">
/// <item><description>语料 / 索引 / checkpoint 一律落在 <c>%TEMP%</c>（构造期硬断言，且不以 <c>D:\data</c> 开头）。</description></item>
/// <item><description>去抖与最大合并等待默认取**长**值（30 s / 60 s）⇒ 冻结时钟下泵任务永不自动 flush，
/// 测试用 internal <c>FlushPendingAsync</c> / <c>RunRecoveryScanAsync</c> 显式驱动，结果确定。</description></item>
/// <item><description>周期补偿 / 体检周期取长值（30 min / 24 h）⇒ 除显式触发外不会自行运行。</description></item>
/// </list>
/// </summary>
internal sealed class MaintenanceRig : IDisposable
{
    internal const long DefaultMaxIndexBytes = 1_073_741_824L;

    private readonly List<IFileContentExtractor> _extractors = new();

    internal MaintenanceRig(
        string tag,
        Func<MaintenanceOptions, MaintenanceOptions>? configureMaintenance = null,
        DateTimeOffset? clockStart = null,
        bool useSystemClock = false,
        params IFileContentExtractor[] extractors)
    {
        Root = Path.Combine(Path.GetTempPath(), "pudding-fts-s3d-" + tag + "-" + Guid.NewGuid().ToString("N"));

        Assert.IsTrue(
            Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"测试根必须在 %TEMP% 下，实际 {Root}");
        Assert.IsFalse(
            Root.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            $"测试根绝不允许落在生产数据根下，实际 {Root}");

        Corpus = Path.Combine(Root, "corpus");
        IndexRoot = Path.Combine(Root, "index");
        Directory.CreateDirectory(Corpus);

        var parsedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extractor in extractors)
        {
            _extractors.Add(extractor);
            foreach (var extension in extractor.SupportedExtensions)
                parsedExtensions.Add(extension);
        }

        Options = new FullTextIndexOptions
        {
            IndexRootDirectory = IndexRoot,
            ParsedExtensions = parsedExtensions,
        };

        var maintenance = new MaintenanceOptions
        {
            Enabled = true,
            Scopes = new[] { Corpus },
            IndexRootDirectory = IndexRoot,
            // 冻结时钟下永不自然到期 ⇒ 由测试显式驱动（确定性）
            Debounce = TimeSpan.FromSeconds(30),
            MaxCoalesceWait = TimeSpan.FromSeconds(60),
            RecoveryScanInterval = TimeSpan.FromMinutes(30),
            HealthCheckInterval = TimeSpan.FromHours(24),
        };

        MaintenanceOptions = configureMaintenance?.Invoke(maintenance) ?? maintenance;

        var all = new List<IFileContentExtractor>(_extractors) { new PlainTextExtractor(Options) };

        Search = new LuceneSearchEngine(Options, new JiebaAnalyzer(), all.ToArray());
        InnerEngine = new LuceneFullTextIndexMaintenanceEngine(
            Search,
            Options,
            new FileSupplyLease(Options),
            MaintenanceOptions.DefaultLeaseWaitUpperBound,
            new SearchEngineScopeReaderInvalidation(Search));
        Engine = new RecordingMaintenanceEngine(InnerEngine);

        // 冻结时钟：默认取「真实现在 + 1 小时」，于是测试刚开始创建的文件（mtime = 真实现在）
        // 一律早于水位线 ⇒ 「未变文件不被重复写」在**不推进时钟**的前提下也成立。
        // 需要「泵任务自然到期」的用例（例如真实 FileSystemWatcher 的端到端）显式改用系统时钟。
        FakeClock = useSystemClock
            ? null
            : new FakeTimeProvider(clockStart ?? DateTimeOffset.UtcNow.AddHours(1));
        Clock = (TimeProvider?)FakeClock ?? TimeProvider.System;

        Pressure = new FixedPressureProbe { Next = new ResourcePressureSample(1.0, null, "test-idle") };

        Maintenance = new LuceneFullTextIndexMaintenance(Options, MaintenanceOptions, Engine, Pressure, Clock);

        Scopes = new List<FullTextMaintenanceScope>
        {
            new(Corpus, FullTextChangeCoalescer.NormalizeComparisonKey(Corpus), DefaultMaxIndexBytes),
        };
    }

    internal string Root { get; }

    internal string Corpus { get; }

    internal string IndexRoot { get; }

    internal FullTextIndexOptions Options { get; }

    internal MaintenanceOptions MaintenanceOptions { get; }

    internal LuceneSearchEngine Search { get; }

    internal LuceneFullTextIndexMaintenanceEngine InnerEngine { get; }

    internal RecordingMaintenanceEngine Engine { get; }

    internal LuceneFullTextIndexMaintenance Maintenance { get; }

    internal TimeProvider Clock { get; }

    internal FakeTimeProvider? FakeClock { get; }

    /// <summary>推进（冻结的）业务时钟；系统时钟下非法。</summary>
    internal void Advance(TimeSpan delta) =>
        (FakeClock ?? throw new InvalidOperationException("系统时钟下不能推进业务时钟")).Advance(delta);

    internal FixedPressureProbe Pressure { get; }

    internal IReadOnlyList<FullTextMaintenanceScope> Scopes { get; }

    internal string ScopeIndexDirectory => FullTextIndexPaths.ResolveIndexDirectory(IndexRoot, Corpus);

    internal string StateDirectory =>
        MaintenanceCheckpoint.ResolveStateDirectory(IndexRoot, Corpus);

    internal string CheckpointPath => MaintenanceCheckpoint.ResolveCheckpointPath(IndexRoot, Corpus);

    internal static string Normalize(string fullPath) =>
        FullTextChangeCoalescer.NormalizeComparisonKey(fullPath);

    internal string CorpusFile(string relativePath) => Path.Combine(Corpus, relativePath);

    internal string WriteCorpusFile(string relativePath, string content)
    {
        var fullPath = CorpusFile(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    /// <summary>把文件 mtime 拨到「业务时钟的当前时刻」—— 让水位线判定认为它「刚刚变过」。</summary>
    internal void TouchAfterWatermark(string fullPath) =>
        File.SetLastWriteTimeUtc(fullPath, Clock.GetUtcNow().UtcDateTime);

    internal FullTextIndexResult BuildInitialIndex() => Search.BuildIndexAsync(Corpus).GetAwaiter().GetResult();

    internal async Task<int> CountHitsAsync(string marker)
    {
        var result = await Search.SearchAsync(marker, Corpus, maxResults: 50);
        return result.Matches.Count;
    }

    internal async Task<List<IndexedPathEntry>> InventoryAsync()
    {
        var entries = new List<IndexedPathEntry>();
        await foreach (var entry in InnerEngine.EnumerateIndexedPathsAsync(Corpus))
            entries.Add(entry);

        return entries;
    }

    internal FullTextMaintenanceSnapshot Snapshot() => Maintenance.GetSnapshot();

    internal FullTextMaintenanceScopeSnapshot ScopeSnapshot()
    {
        var snapshot = Snapshot();
        Assert.AreEqual(1, snapshot.Scopes.Count, "本测试台只有一个 scope");
        return snapshot.Scopes[0];
    }

    internal async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return true;

            await Task.Delay(10);
        }

        return false;
    }

    /// <summary>目录的（文件数, 总字节）—— 用于「探针只读：前后逐位相同」的证据。</summary>
    internal static (int Files, long Bytes) MeasureDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return (0, 0);

        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        long bytes = 0;
        foreach (var file in files)
            bytes += new FileInfo(file).Length;

        return (files.Length, bytes);
    }

    public void Dispose()
    {
        // ★ 先停维护器：否则 watcher / 泵任务 / 体检线程会持有目录句柄，删除必然失败
        //   —— 这正是「失败用例留下 %TEMP% 残留」的机理（断言失败时用例不会走到 StopAsync）。
        try
        {
            Maintenance.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 停止失败不改变「用例已经失败」的事实。
        }

        try
        {
            Search.Dispose();
        }
        catch (IOException)
        {
            // 释放失败不应把用例判红。
        }

        // Windows 上句柄释放有延迟 ⇒ 重试删除（残留由父级脚本单独取证）。
        for (var attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
