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

        public SwitchHarness(CodeSourceMaintenanceMode mode = CodeSourceMaintenanceMode.Coordinator)
        {
            _fixture = CodeIndexFixture.Create();
            _clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
            _indexer = new RecordingCodeIndexer();
            Updater = new ScriptedBatchUpdater();

            var resolver = new DefaultCodeWorkspaceResolver(_fixture.Store);
            _scheduler = new CodeIndexScheduler(_indexer, resolver, _fixture.Store, NullLogger<CodeIndexScheduler>.Instance);
            _calibration = new CodeIndexCalibrationService(_fixture.Store, _clock, logger: NullLogger<CodeIndexMaintenanceService>.Instance);

            if (mode == CodeSourceMaintenanceMode.Coordinator)
            {
                var scanService = new CodeSourceScanService(
                    new FileSystemCodeSourceScanner(new PermissiveIgnoreRules()),
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

        public async Task StartAsync()
        {
            await _fixture.Store.UpsertProjectAsync(new CodeProjectRecord(
                MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, Root, CodeProjectStatus.Active));

            await Service.StartAsync();
            Assert.IsTrue(Service.EnsureScope(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId, Root));
        }

        public void PublishChange(string relativePath, IndexChangeKind kind)
        {
            var watcher = _watcherFactory.Watcher;
            var observedAt = _clock.GetUtcNow();
            watcher.State.MarkObserved(observedAt);

            Assert.IsTrue(watcher.Queue.TryPublish(MaintenanceTestData.Change(
                Root, relativePath, kind, watcher.State.NextSequence(), observedAt)));
        }

        public void AdvancePastDebounce() => _clock.Advance(TimeSpan.FromSeconds(3));

        public CodeIndexMaintenanceScopeStatus Status() =>
            Service.GetScopeStatus(MaintenanceTestData.WorkspaceId, MaintenanceTestData.ScopeId)
            ?? throw new InvalidOperationException("scope not attached");


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
