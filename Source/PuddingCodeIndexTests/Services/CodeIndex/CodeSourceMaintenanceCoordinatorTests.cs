using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// D4 维护协调器门禁：把「校准 → 计划 → 语言批量 → 稳定读 → 原子替换 → 账本提交」串起来，
/// 并锁定三条最重要的不变量：
/// <list type="bullet">
///   <item><description><b>顺序</b>：索引/指纹先原子提交，账本才提交（水位只在真正提交后前进）。</description></item>
///   <item><description><b>失败不掩盖</b>：语言报待重试 / 稳定读失败 ⇒ 记退避 + 计入未解决 ⇒ 水位<b>不</b>前进。</description></item>
///   <item><description><b>不升级整仓</b>：单个文件失败只影响它自己；一批只调用语言接缝一次。</description></item>
/// </list>
/// </summary>
[TestClass]
public sealed class CodeSourceMaintenanceCoordinatorTests : IDisposable
{
    private const string WorkspaceId = "ws-coord";
    private const string ScopeId = "scope-coord";
    private const string Provider = "csharp";

    private string _root = null!;
    private string _scopeRoot = null!;
    private SqliteCodeIndexStore _store = null!;
    private DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(1);

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-coordinator-tests", Guid.NewGuid().ToString("N"));
        _scopeRoot = Path.Combine(_root, "repo", "src");
        Directory.CreateDirectory(_scopeRoot);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "db", "code-index.db"));
    }

    private static CodeConsumerInputFingerprint Consumer(string policy = "policy-1", string semantic = "semantic-1") =>
        new(Provider, policy, semantic);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_scopeRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    private CodeSourceMaintenanceRunOptions Options(
        IReadOnlyCollection<CodeConsumerInputFingerprint>? consumers = null,
        IReadOnlyCollection<string>? hints = null,
        IReadOnlyCollection<string>? changedDuringScan = null) =>
        new(consumers ?? [Consumer()], ProjectFilePaths: [], WatcherHints: hints, ChangedDuringScan: changedDuringScan);

    private (CodeSourceMaintenanceCoordinator Coordinator, FakeScanner Scanner, FakeBatchUpdater Updater) Arrange(
        bool withBatchUpdater = true,
        CodeSourceFingerprintReader? fingerprintReader = null)
    {
        var scanner = new FakeScanner();
        var updater = new FakeBatchUpdater();
        var scanService = new CodeSourceScanService(
            scanner,
            _store,
            new FixedTimeProvider(() => _now),
            logger: NullLogger<CodeSourceScanService>.Instance);

        var coordinator = new CodeSourceMaintenanceCoordinator(
            scanService,
            _store,
            _store,
            withBatchUpdater ? updater : null,
            _store,
            fingerprintReader,
            timeProvider: new FixedTimeProvider(() => _now),
            logger: NullLogger<CodeSourceMaintenanceCoordinator>.Instance);

        return (coordinator, scanner, updater);
    }

    [TestMethod]
    public async Task ExtractsOncePerRunThroughTheBatchSeamAndCommitsAtomically()
    {
        var first = WriteFile("A.cs", "class A { }");
        var second = WriteFile("B.cs", "class B { }");

        var (coordinator, scanner, updater) = Arrange();
        scanner.SetEntries(first, second);

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.AreEqual(1, updater.CallCount, "一批只调用语言接缝一次");
        CollectionAssert.AreEquivalent(
            new[] { first, second }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            updater.LastFilePaths!.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());

        Assert.AreEqual(2, result.ExtractedFileCount);
        Assert.AreEqual(CodeSourceCommitOutcome.Committed, result.LedgerOutcome);
        Assert.IsTrue(result.ScanWatermarkAdvanced, "完整扫描 + 全部提交成功 ⇒ 水位前进");
        Assert.AreEqual("fake-session", result.LanguageSessionKey);

        Assert.IsTrue((await _store.GetSymbolsByFileAsync(WorkspaceId, ScopeId, first)).Count > 0);
        Assert.IsTrue((await _store.GetSymbolsByFileAsync(WorkspaceId, ScopeId, second)).Count > 0);

        var snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.HasCount(2, snapshot.Manifest);
        Assert.IsTrue(snapshot.Manifest[first].Fingerprint.ContentHash.Length > 0, "指纹必须来自真实内容");
        Assert.AreEqual(1, snapshot.Ledger.CommittedVersion);
    }

    [TestMethod]
    public async Task TheSecondRunOfAnUnchangedTreeDoesNothing()
    {
        var file = WriteFile("A.cs", "class A { }");
        var (coordinator, scanner, updater) = Arrange();
        scanner.SetEntries(file);

        var first = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());
        Assert.AreEqual(1, first.ExtractedFileCount);

        updater.Reset();
        var second = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.AreEqual(0, updater.CallCount, "指纹未变 ⇒ 不再提取、不再读正文");
        Assert.AreEqual(0, second.ExtractedFileCount);
        Assert.AreEqual(0, second.RetryableFileCount);
    }

    [TestMethod]
    public async Task ALanguageFailureHoldsTheWatermarkAndRecordsABackoff()
    {
        var good = WriteFile("A.cs", "class A { }");
        var bad = WriteFile("B.cs", "class B { }");

        var (coordinator, scanner, updater) = Arrange();
        scanner.SetEntries(good, bad);
        updater.RetryablePaths.Add(bad);
        updater.Reason = "extractor crashed";

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.AreEqual(1, result.ExtractedFileCount, "失败路径只影响它自己");
        Assert.AreEqual(1, result.RetryableFileCount);
        Assert.AreEqual(CodeSourceCommitOutcome.Superseded, result.LedgerOutcome);
        Assert.IsFalse(result.ScanWatermarkAdvanced, "有未解决路径 ⇒ 水位不前进（宁可重放，不可漏掉）");

        var snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.IsTrue(snapshot.Ledger.PendingRetries.ContainsKey(bad));
        Assert.AreEqual(1, snapshot.Ledger.PendingRetries[bad].Attempts);
        Assert.AreEqual(1, snapshot.Ledger.CommittedVersion, "成功那份仍然提交了");
    }

    [TestMethod]
    public async Task APathUnderBackoffIsNotRetriedAgainThisRound()
    {
        var file = WriteFile("A.cs", "class A { }");
        var (coordinator, scanner, updater) = Arrange();
        scanner.SetEntries(file);
        updater.RetryablePaths.Add(file);
        updater.Reason = "boom";

        var first = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());
        Assert.AreEqual(1, first.RetryableFileCount);

        // 同一个时间点再跑：退避未到期 ⇒ 不进执行组（语言接缝都不该被调用）。
        updater.Reset();
        var second = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.AreEqual(0, updater.CallCount, "退避中的路径不该立刻重试");
        Assert.AreEqual(1, second.RetryableFileCount, "它仍然算未解决，所以水位还是不前进");
        Assert.IsFalse(second.ScanWatermarkAdvanced);
    }

    [TestMethod]
    public async Task RetryBecomesDueAfterTheBackoffWindow()
    {
        var file = WriteFile("A.cs", "class A { }");
        var (coordinator, scanner, updater) = Arrange();
        scanner.SetEntries(file);
        updater.RetryablePaths.Add(file);
        updater.Reason = "boom";

        await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        // 退避阶梯底数是 60s：把时钟推过去，这一轮就该再试。
        _now = _now.AddMinutes(2);
        updater.Reset();
        updater.RetryablePaths.Clear();

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.AreEqual(1, updater.CallCount, "退避到期后必须重试");
        Assert.AreEqual(0, result.RetryableFileCount);
        Assert.AreEqual(CodeSourceCommitOutcome.Committed, result.LedgerOutcome);
        Assert.IsTrue(result.ScanWatermarkAdvanced);
    }

    [TestMethod]
    public async Task AnUnstableFingerprintReadDefersInsteadOfCommitting()
    {
        var file = WriteFile("A.cs", "class A { }");

        // 真实读写竞争无法在测试里可靠复现：用替身让「读不稳定」这件事确定性地发生。
        // 这一分支恰恰最不能出错 —— 一旦用旧内容配新 stat 提交，这份文件就永远不再被核验。
        var (coordinator, scanner, _) = Arrange(fingerprintReader: new UnstableReader());

        scanner.SetEntries(file);

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.AreEqual(0, result.ExtractedFileCount);
        Assert.AreEqual(1, result.RetryableFileCount);
        Assert.IsFalse(result.ScanWatermarkAdvanced);

        var snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.IsFalse(snapshot.Manifest.ContainsKey(file), "不稳定读绝不写指纹");
        Assert.IsTrue(snapshot.Ledger.PendingRetries.ContainsKey(file));
        Assert.IsEmpty(await _store.GetSymbolsByFileAsync(WorkspaceId, ScopeId, file), "不稳定读绝不写索引");
    }

    [TestMethod]
    public async Task DeletesOnlyHappenWhenTheScanIsComplete()
    {
        var kept = WriteFile("A.cs", "class A { }");
        var removed = WriteFile("Gone.cs", "class Gone { }");
        var (coordinator, scanner, _) = Arrange();
        scanner.SetEntries(kept, removed);

        await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        // 现在这个文件从磁盘消失，但扫描不完整：不得删除。
        File.Delete(removed);
        scanner.SetEntries(kept);
        scanner.Incomplete = true;

        var incomplete = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());
        Assert.AreEqual(0, incomplete.DeletedFileCount, "不完整扫描不得删任何东西");
        var snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.IsTrue(snapshot.Manifest.ContainsKey(removed), "不完整扫描下旧记录必须保留");

        // 完整扫描之后才确认删除：索引行与 manifest 行都消失。
        scanner.Incomplete = false;
        var complete = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());
        Assert.AreEqual(1, complete.DeletedFileCount);

        snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.IsFalse(snapshot.Manifest.ContainsKey(removed));
        Assert.IsEmpty(await _store.GetSymbolsByFileAsync(WorkspaceId, ScopeId, removed));
    }

    [TestMethod]
    public async Task PathsWithoutAnyConsumerAreRecordedWithAFingerprintAndResolved()
    {
        // 语言接缝对这条路径报 NotApplicable（能力路由结果）：仍要如实记指纹并按「视图已最新」推进，
        // 否则每轮扫描都会把它当新文件重新处理。
        var file = WriteFile("notes.md", "# notes");
        var (coordinator, scanner, updater) = Arrange();
        scanner.SetEntries(file);
        updater.NotApplicablePaths.Add(file);

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.AreEqual(1, result.NotApplicableFileCount);
        Assert.AreEqual(0, result.RetryableFileCount, "没有消费者认领不是失败");
        Assert.AreEqual(CodeSourceCommitOutcome.Committed, result.LedgerOutcome);
        Assert.IsTrue(result.ScanWatermarkAdvanced);

        var snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.IsTrue(snapshot.Manifest.ContainsKey(file), "指纹要落地，下一轮才会命中「未变」");
        Assert.AreEqual(1, snapshot.Manifest[file].AppliedVersions.Count, "消费者视图推进到捕获版本");
    }

    [TestMethod]
    public async Task WithoutABatchCapabilityNothingIsEscalatedAndEverythingIsRetried()
    {
        var file = WriteFile("A.cs", "class A { }");
        var (coordinator, scanner, _) = Arrange(withBatchUpdater: false);
        scanner.SetEntries(file);

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.AreEqual(0, result.ExtractedFileCount);
        Assert.AreEqual(1, result.RetryableFileCount, "没有批量能力 ⇒ 按路径退避重试，而不是整仓重建");
        Assert.IsFalse(result.ScanWatermarkAdvanced);
    }

    [TestMethod]
    public async Task CapabilityMissingIsReadOnly()
    {
        var file = WriteFile("A.cs", "class A { }");
        var scanner = new FakeScanner();
        scanner.SetEntries(file);

        // 不实现维护能力的 store 替身：整轮只读。
        var coordinator = new CodeSourceMaintenanceCoordinator(
            new CodeSourceScanService(
                scanner, store: null, new FixedTimeProvider(() => _now), logger: NullLogger<CodeSourceScanService>.Instance),
            _store,
            maintenanceStore: null,
            batchUpdater: null,
            graph: null,
            timeProvider: new FixedTimeProvider(() => _now),
            logger: NullLogger<CodeSourceMaintenanceCoordinator>.Instance);

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options());

        Assert.IsTrue(result.CapabilityMissing);
        Assert.AreEqual(0, result.ExtractedFileCount);
        Assert.IsEmpty(await _store.ListFilesAsync(WorkspaceId, ScopeId));
    }

    public void Dispose()
    {
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

    /// <summary>语言接缝替身：按脚本产出 payload / 待重试 / 不适用，并可在提取时改动文件（模拟读写竞争）。</summary>
    private sealed class FakeBatchUpdater : ICodeIndexFileBatchUpdater
    {
        public int CallCount { get; private set; }

        public IReadOnlyCollection<string>? LastFilePaths { get; private set; }

        public HashSet<string> RetryablePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> NotApplicablePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Reason { get; set; } = "scripted failure";

        public Action<string>? TouchFileWhileExtracting { get; set; }

        public void Reset()
        {
            CallCount = 0;
            LastFilePaths = null;
        }

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
                if (NotApplicablePaths.Contains(path))
                {
                    outcomes.Add(new CodeFileIndexOutcome(
                        path, CodeIndexConsumerStatus.NotApplicable, Reason: "not ours"));
                    continue;
                }

                if (RetryablePaths.Contains(path))
                {
                    outcomes.Add(new CodeFileIndexOutcome(
                        path, CodeIndexConsumerStatus.Retryable, Reason: Reason));
                    continue;
                }

                TouchFileWhileExtracting?.Invoke(path);

                var symbols = new List<CodeSymbolRecord>
                {
                    new(workspace.WorkspaceId, workspace.ProjectId, path, $"sym:{path}", "Symbol",
                        CodeSymbolKind.Class, 1, 2, "class Symbol", null),
                };

                outcomes.Add(new CodeFileIndexOutcome(
                    path,
                    CodeIndexConsumerStatus.Applied,
                    new CodeFileIndexPayload(path, symbols, [], [], "C#")));
            }

            return Task.FromResult(
                new CodeIndexFileBatchResult(outcomes, context.ConfigurationFingerprint, "fake-session"));
        }
    }

    /// <summary>可编程的磁盘枚举替身。</summary>
    private sealed class FakeScanner : ICodeSourceScanner
    {
        private readonly List<CodeSourceDiskEntry> _entries = [];

        public bool Incomplete { get; set; }

        public void SetEntries(params string[] filePaths)
        {
            _entries.Clear();

            foreach (var path in filePaths)
            {
                var info = new FileInfo(path);
                _entries.Add(new CodeSourceDiskEntry(
                    path, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length));
            }
        }

        public Task<CodeSourceScanOutcome> ScanAsync(string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeSourceScanOutcome(
                _entries.ToArray(),
                RootUsable: true,
                Complete: !Incomplete,
                IncompleteReason: Incomplete ? "subtree unreadable" : null));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly Func<DateTimeOffset> _now;

        public FixedTimeProvider(Func<DateTimeOffset> now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now();
    }

    /// <summary>恒返回「读不稳定」的读取器替身（确定性验证弃用分支）。</summary>
    private sealed class UnstableReader : CodeSourceFingerprintReader
    {
        public override Task<CodeSourceReadResult> ReadAsync(
            string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeSourceReadResult(null, Stable: false, Reason: "file changed while it was being read"));
    }
}
