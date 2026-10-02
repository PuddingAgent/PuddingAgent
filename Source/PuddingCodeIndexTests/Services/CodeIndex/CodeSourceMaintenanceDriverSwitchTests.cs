using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using PuddingCodeIndex.Services.CodeIndex;
using PuddingCodeIndex.Storage;
using PuddingCodeIndexTests.Services;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// D4 驱动切换门禁（2026-10-02）：<see cref="CodeSourceMaintenanceMode.Coordinator"/> 打开时，
/// 一批变更交给源维护协调器；<see cref="CodeSourceMaintenanceMode.Legacy"/>（默认）行为不变。
/// <para>
/// 最重要的一条：单路径失败在新链路里**按退避重试**，不再把整个 scope 重跑
/// （旧路径的「一个文件不认就升级整仓」正是被诊断出来的放大之一）。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSourceMaintenanceDriverSwitchTests
{
    [TestMethod]
    public async Task CoordinatorModeRoutineCalibrationSweepsStaleRowsThroughTheCoordinator()
    {
        using var harness = new SwitchHarness();
        await harness.StartAsync();

        // 「索引里有、磁盘上没有」的陈旧行 + 一条 manifest 记录。
        var gone = Path.Combine(harness.Root, "Gone.cs");
        await harness.SeedIndexedAbsoluteAsync(gone);
        await harness.SeedManifestAsync(gone);

        // 常规校准钟（默认 15 分钟）到期后才校准。
        harness.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod + TimeSpan.FromMinutes(1));
        await harness.Service.ProcessDueBatchesAsync();

        Assert.IsEmpty(
            await harness.Store.GetSymbolsByFileAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, gone),
            "陈旧行的清理必须走协调器（索引与 manifest 一起变）");
        Assert.IsFalse(
            (await harness.Store.LoadSourceMaintenanceAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId))
                .Manifest.ContainsKey(gone),
            "manifest 记录必须与索引一起消失，否则两条链路会分叉");
        Assert.IsTrue(harness.Status().SweptFileCount >= 1);
        Assert.IsFalse(harness.Status().NeedsReconcile, "完整扫描完成后标位应被清除");
    }

    [TestMethod]
    public async Task CoordinatorModeRefusedRootKeepsTheScopeFlagged()
    {
        using var harness = new SwitchHarness();
        var missingRoot = Path.Combine(harness.Root, "not-mounted");
        await harness.StartWithRootAsync(missingRoot);

        harness.Advance(CodeIndexMaintenanceService.DefaultCalibrationPeriod + TimeSpan.FromMinutes(1));
        await harness.Service.ProcessDueBatchesAsync();

        var status = harness.Status();
        Assert.IsTrue(status.NeedsReconcile, "根不可用 ⇒ 必须保持标位（绝不把「读不到」当成「没有陈旧行」）");
        Assert.IsTrue(status.RejectedCalibrationRunCount >= 1);
        Assert.AreEqual(0, status.SweptFileCount, "根不可用时一个文件都不许删");
    }

    [TestMethod]
    public async Task CoordinatorModeRoutesAReconcileBatchThroughAFullScanInsteadOfTheOldScopeRun()
    {
        using var harness = new SwitchHarness();
        var file = harness.WriteFile("A.cs", "class A { }");
        await harness.StartAsync();

        // 队列溢出 ⇒ 细粒度捕获不可信 ⇒ 必须完整扫描；旧的升级路径会直接跑 scope 级索引而不更新 manifest。
        harness.MarkNeedsReconcile();
        harness.PublishChange("A.cs", IndexChangeKind.Changed);
        harness.AdvancePastDebounce();

        await harness.Service.ProcessDueBatchesAsync();

        var (full, probe) = harness.ScanCounts();
        Assert.IsTrue(full >= 1, "reconcile 批次必须走完整枚举");
        Assert.AreEqual(0, probe, "不得退化成按路径观测");
        Assert.AreEqual(0, harness.Status().ScopeEscalationCount, "不再升级成旧的 scope 级运行");
        Assert.IsTrue(
            (await harness.Store.GetSymbolsByFileAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, file)).Count > 0,
            "完整扫描同样把索引写进去");
    }

    [TestMethod]
    public async Task ScopeStatusExposesSourceMaintenanceFactsForAcceptance()
    {
        using var harness = new SwitchHarness();
        var file = harness.WriteFile("A.cs", "class A { }");
        await harness.StartAsync();
        harness.PublishChange("A.cs", IndexChangeKind.Changed);
        harness.AdvancePastDebounce();

        await harness.Service.ProcessDueBatchesAsync();

        var status = harness.Status();
        Assert.AreEqual(CodeSourceMaintenanceMode.Coordinator, status.SourceMaintenanceMode);
        Assert.AreEqual(1, status.SourceMaintenanceRunCount);
        Assert.AreEqual(1, status.SourceMaintenanceExtractedFileCount);
        Assert.AreEqual(0, status.SourceMaintenanceUnresolvedPathCount, "这一轮没有未解决路径");
        Assert.AreEqual("switch-session", status.LastSourceMaintenanceSessionKey, "语言侧复用的快照标识可读");

        // 第二轮（提示相同但内容未变）：复用计数上升、提取计数不变。
        harness.PublishChange("A.cs", IndexChangeKind.Changed);
        harness.AdvancePastDebounce();
        await harness.Service.ProcessDueBatchesAsync();

        status = harness.Status();
        Assert.AreEqual(2, status.SourceMaintenanceRunCount);
        Assert.AreEqual(1, status.SourceMaintenanceExtractedFileCount, "内容未变 ⇒ 不再提取");
        Assert.AreEqual(1, status.SourceMaintenanceReusedFileCount, "指纹一致 ⇒ 跳过提取");
    }
    [TestMethod]
    public async Task CoordinatorModeRunsTheChainAndDoesNotEscalateTheScope()
    {
        using var harness = new SwitchHarness();
        var file = harness.WriteFile("A.cs", "class A { }");
        await harness.StartAsync();
        harness.PublishChange("A.cs", IndexChangeKind.Changed);
        harness.AdvancePastDebounce();

        var processed = await harness.Service.ProcessDueBatchesAsync();

        Assert.IsTrue(processed >= 1, "这一批应当被处理");
        Assert.AreEqual(1, harness.Updater.CallCount, "语言批量接缝必须被调用");
        CollectionAssert.Contains(harness.Updater.LastFilePaths!.ToArray(), file);

        Assert.IsTrue(
            (await harness.Store.GetSymbolsByFileAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, file)).Count > 0,
            "协调器必须把索引真正写进去（原子替换）");

        Assert.AreEqual(0, harness.Status().ScopeEscalationCount, "新链路成功时不得升级为整仓运行");

        var snapshot = await harness.Store.LoadSourceMaintenanceAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.IsTrue(snapshot.Manifest.ContainsKey(file), "指纹必须落地");
    }

    [TestMethod]
    public async Task CoordinatorModeHoldsABadPathBackInsteadOfEscalatingTheScope()
    {
        using var harness = new SwitchHarness();
        var good = harness.WriteFile("A.cs", "class A { }");
        var bad = harness.WriteFile("B.cs", "class B { }");
        harness.Updater.RetryablePaths.Add(bad);
        await harness.StartAsync();
        harness.PublishChange("A.cs", IndexChangeKind.Changed);
        harness.PublishChange("B.cs", IndexChangeKind.Changed);
        harness.AdvancePastDebounce();

        await harness.Service.ProcessDueBatchesAsync();

        Assert.IsTrue(
            (await harness.Store.GetSymbolsByFileAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, good)).Count > 0,
            "好路径照常提交");
        Assert.AreEqual(0, harness.Status().ScopeEscalationCount, "坏路径只影响它自己：绝不升级整仓");

        var snapshot = await harness.Store.LoadSourceMaintenanceAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId);
        Assert.IsTrue(snapshot.Ledger.PendingRetries.ContainsKey(bad), "坏路径记入退避，下一轮再试");
    }

    [TestMethod]
    public async Task LegacyModeStaysTheDefaultAndStillUsesThePerFilePath()
    {
        using var harness = new SwitchHarness(mode: CodeSourceMaintenanceMode.Legacy);
        var file = harness.WriteFile("A.cs", "class A { }");
        await harness.StartAsync();
        harness.PublishChange("A.cs", IndexChangeKind.Changed);
        harness.AdvancePastDebounce();

        await harness.Service.ProcessDueBatchesAsync();

        Assert.AreEqual(0, harness.Updater.CallCount, "旧路径不使用批量接缝");
        Assert.AreEqual(
            0,
            (await harness.Store.LoadSourceMaintenanceAsync(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId)).Manifest.Count,
            "旧路径不写 manifest（它的状态在别处）");
    }

    [TestMethod]
    public void CoordinatorModeWithoutItsPartsFallsBackToLegacyInsteadOfPretending()
    {
        var fixture = CodeIndexFixture.Create();
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
            var indexer = new RecordingCodeIndexer();
            var resolver = new DefaultCodeWorkspaceResolver(fixture.Store);
            var scheduler = new CodeIndexScheduler(indexer, resolver, fixture.Store, NullLogger<CodeIndexScheduler>.Instance);

            // 开关打开但协调器/消费者输入缺失：必须退回旧路径（而不是静默假装新链路在跑）。
            var service = new CodeIndexMaintenanceService(
                scheduler,
                new FakeCodeIndexWatcherFactory(),
                fixture.Store,
                indexer,
                resolver,
                NullLogger<CodeIndexMaintenanceService>.Instance,
                clock,
                pollInterval: TimeSpan.FromHours(1),
                options: new CodeIndexMaintenanceOptions(CodeSourceMaintenanceMode.Coordinator));

            Assert.IsNotNull(service);
            service.Dispose();
            scheduler.Dispose();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>把真实组件按生产方式装起来，只把语言侧换成脚本化替身。</summary>
    private sealed class SwitchHarness : IDisposable
    {
        private readonly CodeIndexFixture _fixture;
        private readonly MutableTimeProvider _clock;
        private readonly RecordingCodeIndexer _indexer;
        private readonly CodeIndexScheduler _scheduler;
        private readonly CodeIndexCalibrationService _calibration;
        private readonly CodeSourceMaintenanceCoordinator? _coordinator;
        private readonly FakeCodeIndexWatcherFactory _watcherFactory = new();
        private readonly CountingScanner? _scanner;

        public SwitchHarness(CodeSourceMaintenanceMode mode = CodeSourceMaintenanceMode.Coordinator)
        {
            _fixture = CodeIndexFixture.Create();
            _clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1));
            _indexer = new RecordingCodeIndexer();
            Updater = new ScriptedBatchUpdater();

            var resolver = new DefaultCodeWorkspaceResolver(_fixture.Store);
            _scheduler = new CodeIndexScheduler(_indexer, resolver, _fixture.Store, NullLogger<CodeIndexScheduler>.Instance);
            _calibration = new CodeIndexCalibrationService(_fixture.Store, _clock, logger: NullLogger<CodeIndexMaintenanceService>.Instance);

            if (mode == CodeSourceMaintenanceMode.Coordinator)
            {
                _scanner = new CountingScanner(new FileSystemCodeSourceScanner(new PermissiveIgnoreRules()));

                var scanService = new CodeSourceScanService(
                    _scanner,
                    _fixture.Store,
                    _clock,
                    logger: NullLogger<CodeSourceScanService>.Instance);

                _coordinator = new CodeSourceMaintenanceCoordinator(
                    scanService,
                    _fixture.Store,
                    _fixture.Store,
                    Updater,
                    _fixture.Store,
                    timeProvider: _clock,
                    logger: NullLogger<CodeSourceMaintenanceCoordinator>.Instance);
            }

            Service = new CodeIndexMaintenanceService(
                _scheduler,
                _watcherFactory,
                _fixture.Store,
                _indexer,
                resolver,
                NullLogger<CodeIndexMaintenanceService>.Instance,
                _clock,
                pollInterval: TimeSpan.FromHours(1),
                calibration: _calibration,
                options: new CodeIndexMaintenanceOptions(mode),
                sourceMaintenance: _coordinator,
                consumerInputs: mode == CodeSourceMaintenanceMode.Coordinator
                    ? new StaticConsumerInputs()
                    : null);
        }

        public CodeIndexMaintenanceService Service { get; }

        public ScriptedBatchUpdater Updater { get; }

        public SqliteCodeIndexStore Store => _fixture.Store;

        public string Root => _fixture.Root;

        public void Dispose()
        {
            Service.Dispose();
            _scheduler.Dispose();
            _fixture.Dispose();
        }

        public string WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(_fixture.Root, relativePath);
            File.WriteAllText(path, content);
            return path;
        }

        public async Task StartAsync() => await StartWithRootAsync(Root);

        public async Task StartWithRootAsync(string root)
        {
            await _fixture.Store.UpsertProjectAsync(new CodeProjectRecord(
                MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, root, CodeProjectStatus.Active));

            await Service.StartAsync();
            Assert.IsTrue(Service.EnsureScope(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, root));
        }

        /// <summary>种一条陈旧索引行（磁盘上并不存在该文件）。</summary>
        public async Task SeedIndexedAbsoluteAsync(string filePath)
        {
            await _fixture.Store.UpsertFilesAsync(
                MaintenanceTestData.WorkspaceId,
                MaintenanceTestData.ScopeId,
                [new CodeFileRecord(
                    MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, filePath, "C#", _clock.GetUtcNow())]);

            await _fixture.Store.UpsertSymbolsAsync(
                MaintenanceTestData.WorkspaceId,
                MaintenanceTestData.ScopeId,
                [new CodeSymbolRecord(
                    MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, filePath,
                    $"seed:{filePath}", "Stale", CodeSymbolKind.Class, 1, 2, "class Stale", null)]);
        }

        public Task SeedManifestAsync(string filePath) =>
            _fixture.Store.SaveSourceManifestAsync(
                MaintenanceTestData.WorkspaceId,
                MaintenanceTestData.ScopeId,
                [new CodeSourceEntry(
                    filePath,
                    new SourceFingerprint(_clock.GetUtcNow(), 10, "seeded"),
                    [new AppliedFileVersion("C#", "policy-1", "semantic-1", 1)])],
                []);

        public void Advance(TimeSpan delta) => _clock.Advance(delta);

        public void PublishChange(string relativePath, IndexChangeKind kind)
        {
            var watcher = _watcherFactory.Watcher;
            var observedAt = _clock.GetUtcNow();
            watcher.State.MarkObserved(observedAt);

            Assert.IsTrue(watcher.Queue.TryPublish(MaintenanceTestData.Change(
                Root, relativePath, kind, watcher.State.NextSequence(), observedAt)));
        }

        public void AdvancePastDebounce() => _clock.Advance(TimeSpan.FromSeconds(3));

        public void MarkNeedsReconcile() =>
            _watcherFactory.Watcher.State.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.QueueOverflow);

        /// <summary>全树枚举 / 按路径观测各自的次数（用来断言 reconcile 走的是完整扫描）。</summary>
        public (int Full, int Probe) ScanCounts() =>
            _scanner is null ? (0, 0) : (_scanner.FullScanCount, _scanner.ProbeCount);

        public CodeIndexMaintenanceScopeStatus Status() =>
            Service.GetScopeStatus(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId)
            ?? throw new InvalidOperationException("scope not attached");


    }

    /// <summary>计数装饰器：分别记录全树枚举与按路径观测的次数。</summary>
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

    private sealed class ScriptedBatchUpdater : ICodeIndexFileBatchUpdater
    {
        public int CallCount { get; private set; }

        public IReadOnlyCollection<string>? LastFilePaths { get; private set; }

        public HashSet<string> RetryablePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<CodeIndexFileBatchResult> UpdateFilesAsync(
            CodeWorkspaceDescriptor workspace,
            IReadOnlyCollection<string> filePaths,
            CodeIndexBatchContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastFilePaths = filePaths.ToArray();

            var outcomes = new List<CodeFileIndexOutcome>();

            foreach (var path in filePaths)
            {
                if (RetryablePaths.Contains(path))
                {
                    outcomes.Add(new CodeFileIndexOutcome(path, CodeIndexConsumerStatus.Retryable, Reason: "scripted"));
                    continue;
                }

                var symbols = new List<CodeSymbolRecord>
                {
                    new(workspace.WorkspaceId, workspace.ProjectId, path, $"sym:{path}", "Symbol",
                        CodeSymbolKind.Class, 1, 2, "class Symbol", null),
                };

                outcomes.Add(new CodeFileIndexOutcome(
                    path, CodeIndexConsumerStatus.Applied, new CodeFileIndexPayload(path, symbols, [], [], "C#")));
            }

            return Task.FromResult(new CodeIndexFileBatchResult(outcomes, context.ConfigurationFingerprint, "switch-session"));
        }
    }

    private sealed class StaticConsumerInputs : ICodeSourceConsumerInputProvider
    {
        public Task<IReadOnlyCollection<CodeConsumerInputFingerprint>> GetConsumerInputsAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<CodeConsumerInputFingerprint>>(
                [new CodeConsumerInputFingerprint("C#", "policy-1", "semantic-1")]);
    }

    private sealed class PermissiveIgnoreRules : ICodeSourceIgnoreRules
    {
        public bool IsIgnored(string absolutePath, bool isDirectory) => false;
    }
}
