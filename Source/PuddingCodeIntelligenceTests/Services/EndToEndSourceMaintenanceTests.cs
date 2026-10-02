using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using PuddingCodeIndex.Services.CodeIndex;
using PuddingCodeIndex.Storage;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIntelligence.Extractors;
using PuddingCodeIntelligence.TypeScript;


namespace PuddingCodeIntelligenceTests.Services;

/// <summary>
/// D4 **启用后链路**的组件级端到端门禁（2026-10-02）：把产品里真实装配的那套对象图
/// （维护驱动 → 协调器 → 校准/计划 → 语言批量接缝 → 稳定读 → 原子替换 → 账本）跑在**真实语言索引器**
/// （Roslyn 真实工作区 + 真实 TS 提取器）与真实临时文件上。
/// <para>
/// 它与既有用例的分工：既有用例各自锁定一层语义（接缝路由、指纹、原子替换、账本…），
/// 这一条回答的是「把它们按生产接线装起来、跑一次真实改动，索引里到底有没有正确的符号」。
/// 进程生命周期/部署验收仍归外部控制器，本类不覆盖那部分。
/// </para>
/// </summary>
[TestClass]
public sealed class EndToEndSourceMaintenanceTests : IDisposable
{
    private const string WorkspaceId = "ws-e2e";
    private const string ScopeId = "scope-e2e";

    private string _root = null!;
    private string _repo = null!;
    private SqliteCodeIndexStore _store = null!;
    private MutableTimeProvider _clock = null!;
    private EndToEndHarness _harness = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-e2e-tests", Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "db", "code-index.db"));
        _clock = new MutableTimeProvider();
    }

    private static bool AssetsAvailable() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Scripts", "extract-ts-symbols.js"));

    [TestMethod]
    public async Task ARealChangeRunsTheWholeEnabledChainOnceAndLandsTheRealSymbols()
    {
        if (!AssetsAvailable())
        {
            Assert.Inconclusive("extractor assets are not present in the test output directory");
            return;
        }

        var alpha = WriteFile("Alpha.cs", "namespace Demo { public class Alpha { public int Run() => 1; } }");
        var beta = WriteFile("beta.ts", "export function betaHelper(): number { return 2; }\n");

        _harness = new EndToEndHarness(_store, _clock, _repo);
        await _harness.StartAsync();

        _harness.PublishChange("Alpha.cs", IndexChangeKind.Changed);
        _harness.PublishChange("beta.ts", IndexChangeKind.Changed);
        _clock.Advance(TimeSpan.FromSeconds(3));

        await _harness.Service.ProcessDueBatchesAsync();

        // ① 真实语言索引器产出的符号真的进了索引。
        var alphaSymbols = await _store.GetSymbolsByFileAsync(WorkspaceId, ScopeId, alpha);
        var betaSymbols = await _store.GetSymbolsByFileAsync(WorkspaceId, ScopeId, beta);
        Assert.IsTrue(
            alphaSymbols.Any(symbol => symbol.Name == "Alpha"),
            $"C# 符号缺失，实际: {string.Join(',', alphaSymbols.Select(symbol => symbol.Name))}");
        Assert.IsTrue(
            betaSymbols.Any(symbol => symbol.Name == "betaHelper"),
            $"TS 符号缺失，实际: {string.Join(',', betaSymbols.Select(symbol => symbol.Name))}");

        // ② 指纹与消费者水位一起落地（原子替换接缝）。
        var snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.IsTrue(snapshot.Manifest.ContainsKey(alpha));
        Assert.IsTrue(snapshot.Manifest.ContainsKey(beta));
        Assert.IsTrue(snapshot.Manifest[alpha].Fingerprint.ContentHash.Length > 0);

        // ③ scope 状态暴露了验收要读的事实。
        var status = _harness.Status();
        Assert.AreEqual(CodeSourceMaintenanceMode.Coordinator, status.SourceMaintenanceMode);
        Assert.AreEqual(1, status.SourceMaintenanceRunCount);
        Assert.AreEqual(2, status.SourceMaintenanceExtractedFileCount);
        Assert.AreEqual(0, status.SourceMaintenanceUnresolvedPathCount);
        Assert.AreEqual(0, status.ScopeEscalationCount, "新链路不该升级成整仓运行");

        // ④ 提示驱动的一轮不得遍历整棵树。
        Assert.AreEqual(0, _harness.Scanner.FullScanCount, "提示驱动只做按路径观测");
        Assert.AreEqual(1, _harness.Scanner.ProbeCount);
    }

    [TestMethod]
    public async Task ARepeatedHintOnUnchangedRealFilesSkipsExtraction()
    {
        if (!AssetsAvailable())
        {
            Assert.Inconclusive("extractor assets are not present in the test output directory");
            return;
        }

        WriteFile("Alpha.cs", "namespace Demo { public class Alpha { public int Run() => 1; } }");
        WriteFile("beta.ts", "export function betaHelper(): number { return 2; }\n");

        _harness = new EndToEndHarness(_store, _clock, _repo);
        await _harness.StartAsync();

        _harness.PublishChange("Alpha.cs", IndexChangeKind.Changed);
        _harness.PublishChange("beta.ts", IndexChangeKind.Changed);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await _harness.Service.ProcessDueBatchesAsync();

        var afterFirst = _harness.Status();
        Assert.AreEqual(2, afterFirst.SourceMaintenanceExtractedFileCount);

        // 同两个提示再来一次（编辑器抖动/重复事件），内容没变。
        _harness.PublishChange("Alpha.cs", IndexChangeKind.Changed);
        _harness.PublishChange("beta.ts", IndexChangeKind.Changed);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await _harness.Service.ProcessDueBatchesAsync();

        var afterSecond = _harness.Status();
        Assert.AreEqual(2, afterSecond.SourceMaintenanceExtractedFileCount, "内容未变 ⇒ 不再提取（真实工具链上同样成立）");
        Assert.AreEqual(2, afterSecond.SourceMaintenanceReusedFileCount, "指纹一致 ⇒ 只刷新消费者视图");
        Assert.AreEqual(0, afterSecond.SourceMaintenanceUnresolvedPathCount);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_repo, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        _harness?.Dispose();

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // temp 目录清理是 best-effort
        }
    }

    /// <summary>按生产方式装配：真实索引器 + 真实扫描器 + 协调器 + 维护驱动（Coordinator 模式）。</summary>
    private sealed class EndToEndHarness : IDisposable
    {
        private readonly CodeIndexScheduler _scheduler;

        public EndToEndHarness(SqliteCodeIndexStore store, MutableTimeProvider clock, string repoRoot)
        {
            Store = store;
            RepoRoot = repoRoot;
            Scanner = new CountingScanner(new FileSystemCodeSourceScanner(new PermissiveIgnoreRules()));
            Watchers = new FakeWatcherFactory();

            // 真实语言索引器：Roslyn 的真实工作区由注入的 opener 提供（测试里用内存工作区，
            // 文档路径指向真实临时文件），TS 用组件自带的真实提取器脚本。
            var roslyn = new RoslynCSharpIndexer(
                store, NullLogger<RoslynCSharpIndexer>.Instance, (_, _) => Task.FromResult<Workspace>(CreateWorkspace(repoRoot)));
            var typescript = new TypeScriptIndexer(store, NullLogger<TypeScriptIndexer>.Instance, new ExtractorAssetResolver());
            Indexer = new CompositeCodeIndexer([roslyn, typescript], NullLogger<CompositeCodeIndexer>.Instance);

            var resolver = new DefaultCodeWorkspaceResolver(store);
            _scheduler = new CodeIndexScheduler(Indexer, resolver, store, NullLogger<CodeIndexScheduler>.Instance);

            var scanService = new CodeSourceScanService(Scanner, store, clock, logger: NullLogger<CodeSourceScanService>.Instance);
            var coordinator = new CodeSourceMaintenanceCoordinator(
                scanService, store, store, Indexer as ICodeIndexFileBatchUpdater, store, timeProvider: clock,
                logger: NullLogger<CodeSourceMaintenanceCoordinator>.Instance);

            var consumerInputs = new LanguageCodeSourceConsumerInputProvider([roslyn, typescript]);

            Service = new CodeIndexMaintenanceService(
                _scheduler,
                Watchers,
                store,
                Indexer,
                resolver,
                NullLogger<CodeIndexMaintenanceService>.Instance,
                clock,
                pollInterval: TimeSpan.FromHours(1),
                options: new CodeIndexMaintenanceOptions(CodeSourceMaintenanceMode.Coordinator),
                sourceMaintenance: coordinator,
                consumerInputs: consumerInputs);
        }

        public CodeIndexMaintenanceService Service { get; }

        public CountingScanner Scanner { get; }

        public FakeWatcherFactory Watchers { get; }

        public ICodeIndexer Indexer { get; }

        public SqliteCodeIndexStore Store { get; }

        public string RepoRoot { get; }

        public async Task StartAsync()
        {
            await Store.UpsertProjectAsync(new CodeProjectRecord(
                WorkspaceId, ScopeId, RepoRoot, CodeProjectStatus.Active));

            await Service.StartAsync();
            Assert.IsTrue(Service.EnsureScope(WorkspaceId, ScopeId, RepoRoot));
        }

        public void PublishChange(string relativePath, IndexChangeKind kind)
        {
            var watcher = Watchers.Watcher;
            var observedAt = DateTimeOffset.UtcNow.AddHours(1);
            watcher.State.MarkObserved(observedAt);

            Assert.IsTrue(watcher.Queue.TryPublish(new IndexChange(
                WorkspaceId,
                ScopeId,
                Path.Combine(RepoRoot, relativePath),
                kind,
                OldFullPath: null,
                IsDirectory: false,
                Sequence: watcher.State.NextSequence(),
                ObservedAtUtc: observedAt)));
        }

        public CodeIndexMaintenanceScopeStatus Status() =>
            Service.GetScopeStatus(WorkspaceId, ScopeId) ?? throw new InvalidOperationException("scope not attached");

        public void Dispose()
        {
            Service.Dispose();
            _scheduler.Dispose();
        }

        /// <summary>把仓库里的真实文件读成内存工作区文档（路径即真实路径）。</summary>
        private static AdhocWorkspace CreateWorkspace(string repoRoot)
        {
            var workspace = new AdhocWorkspace();
            var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
                .ToArray<MetadataReference>();

            var project = workspace.AddProject(
                ProjectInfo.Create(projectId, VersionStamp.Create(), "E2E", "E2E", LanguageNames.CSharp)
                    .WithMetadataReferences(references));

            foreach (var path in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
            {
                workspace.AddDocument(DocumentInfo.Create(
                    DocumentId.CreateNewId(project.Id),
                    Path.GetFileName(path),
                    filePath: path,
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(File.ReadAllText(path)), VersionStamp.Create()))));
            }

            return workspace;
        }
    }

    private sealed class CountingScanner : ICodeSourceScanner, ICodeSourcePathProbe
    {
        private readonly FileSystemCodeSourceScanner _inner;

        public CountingScanner(FileSystemCodeSourceScanner inner) => _inner = inner;

        public int FullScanCount { get; private set; }

        public int ProbeCount { get; private set; }

        public Task<CodeSourceScanOutcome> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            FullScanCount++;
            return _inner.ScanAsync(rootPath, cancellationToken);
        }

        public Task<CodeSourceScanOutcome> ObserveAsync(
            IReadOnlyCollection<string> absolutePaths, CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            return _inner.ObserveAsync(absolutePaths, cancellationToken);
        }
    }

    private sealed class FakeWatcherFactory : ICodeIndexWatcherFactory
    {
        public FakeWatcher Watcher { get; private set; } = null!;

        public ICodeIndexChangeWatcher? Create(
            string workspaceId,
            string scopeId,
            string rootPath,
            CodeIndexChangeQueue queue,
            CodeIndexScopeState state)
        {
            Watcher = new FakeWatcher(queue, state);
            return Watcher;
        }
    }

    private sealed class FakeWatcher : ICodeIndexChangeWatcher
    {
        public FakeWatcher(CodeIndexChangeQueue queue, CodeIndexScopeState state)
        {
            Queue = queue;
            State = state;
        }

        public CodeIndexChangeQueue Queue { get; }

        public CodeIndexScopeState State { get; }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class PermissiveIgnoreRules : ICodeSourceIgnoreRules
    {
        public bool IsIgnored(string absolutePath, bool isDirectory) => false;
    }
}

/// <summary>可推进的测试时钟（本程序集内的最小实现）。</summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset? now = null) => _now = now ?? DateTimeOffset.UtcNow.AddHours(1);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}