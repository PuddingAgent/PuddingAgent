using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;
using PuddingCodeIndex.Services;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// 独立对抗性复核（2026-10-02）发现项的回归锁。复核结论：没有 BLOCKER，但有 4 条 MAJOR ——
/// 都是「顺序/调度/活性」类缺陷，本文件逐条钉住：
/// <list type="number">
///   <item><description>被检测器判 Deferred 的路径**必须**记进持久待办并让水位停住（否则它既不在索引、
///     也不在重试表里，唯一的触发器只剩「再来一次提示」——真正的静默丢失窗口）。</description></item>
///   <item><description>走过 Rebind / 无消费者认领 的路径也要清退避（否则一个从此走这两条分支的路径
///     永远挂着待重试，永久冻结水位）。</description></item>
///   <item><description>删除会连带删掉其他文件的入边 ⇒ 依赖方必须被排进待办。</description></item>
///   <item><description>提取耗时：提交前必须确认 stat 未变，否则索引与指纹会错配。</description></item>
/// </list>
/// </summary>
[TestClass]
public sealed class CodeSourceMaintenanceReviewFixesTests : IDisposable
{
    private const string WorkspaceId = "ws-review";
    private const string ScopeId = "scope-review";

    private string _root = null!;
    private string _scopeRoot = null!;
    private SqliteCodeIndexStore _store = null!;
    private DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(1);

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-review-fixes-tests", Guid.NewGuid().ToString("N"));
        _scopeRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_scopeRoot);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "db", "code-index.db"));
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_scopeRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    private (CodeSourceMaintenanceCoordinator Coordinator, ScriptedUpdater Updater) Arrange(
        ICodeSourceScanner? scanner = null)
    {
        var effectiveScanner = scanner ?? new TreeScanner(permissive: true);
        var updater = new ScriptedUpdater();

        var scanService = new CodeSourceScanService(
            effectiveScanner, _store, new FixedTimeProvider(() => _now), logger: NullLogger<CodeSourceScanService>.Instance);

        var coordinator = new CodeSourceMaintenanceCoordinator(
            scanService,
            _store,
            _store,
            updater,
            _store,
            timeProvider: new FixedTimeProvider(() => _now),
            logger: NullLogger<CodeSourceMaintenanceCoordinator>.Instance);

        return (coordinator, updater);
    }

    private CodeSourceMaintenanceRunOptions Options(bool targeted = true, params string[] hints) =>
        new(
            [new CodeConsumerInputFingerprint("C#", "policy-1", "semantic-1")],
            ProjectFilePaths: [],
            WatcherHints: hints.Length == 0 ? null : hints,
            Targeted: targeted);

    [TestMethod]
    public async Task ACompleteScanRemovesIndexOrphanRowsThatTheManifestNeverKnewAbout()
    {
        // 旧链路时代被索引、但从未进过 manifest（例如后来被 .gitignore 忽略、或已被删除）的行：
        // 新链路永远枚举不到它们，只能由完整扫描轮次清理（否则它们永久占着索引）。
        var orphan = Path.Combine(_scopeRoot, "Legacy", "Gone.cs");
        await _store.UpsertFilesAsync(
            WorkspaceId,
            ScopeId,
            [new CodeFileRecord(WorkspaceId, ScopeId, orphan, "C#", _now)]);

        await _store.UpsertSymbolsAsync(
            WorkspaceId,
            ScopeId,
            [new CodeSymbolRecord(WorkspaceId, ScopeId, orphan, $"sym:{orphan}", "Ghost",
                CodeSymbolKind.Class, 1, 2, "class Ghost", null)]);

        var (coordinator, _) = Arrange();
        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(targeted: false));

        Assert.AreEqual(1, result.OrphanFileCount, "完整扫描必须清掉孤儿行");
        Assert.IsEmpty(
            await _store.GetSymbolsByFileAsync(WorkspaceId, ScopeId, orphan),
            "孤儿行连同它的符号都要消失");
        Assert.IsEmpty(await _store.ListFilesAsync(WorkspaceId, ScopeId));
    }

    [TestMethod]
    public async Task AHintedRunNeverSweepsOrphans()
    {
        var orphan = Path.Combine(_scopeRoot, "Legacy", "Gone.cs");
        await _store.UpsertFilesAsync(
            WorkspaceId, ScopeId, [new CodeFileRecord(WorkspaceId, ScopeId, orphan, "C#", _now)]);

        var (coordinator, _) = Arrange();
        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(true, orphan));

        Assert.AreEqual(0, result.OrphanFileCount, "不完整观测无法证明「它不在磁盘上」⇒ 绝不清理");
        Assert.HasCount(1, await _store.ListFilesAsync(WorkspaceId, ScopeId));
    }

    [TestMethod]
    public async Task AHintedPathThatVanishedIsNotDeletedAndStaysPendingWorkNotSilentlyLost()
    {
        var file = WriteFile("A.cs", "class A { }");
        var (coordinator, updater) = Arrange();

        // 先正常索引一次，让 manifest 有它的指纹。
        var initial = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(true, file));
        Assert.AreEqual(1, initial.ExtractedFileCount);
        Assert.IsTrue((await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId)).Manifest.ContainsKey(file));

        // 文件随后消失，但这一轮只有**提示**（按路径观测 ⇒ 必然不完整）⇒ 删除不能定论，只能 Deferred。
        File.Delete(file);
        updater.Reset();

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(true, file));

        Assert.AreEqual(0, result.DeletedFileCount, "不完整观测绝不确认删除");
        Assert.AreEqual(1, result.RetryableFileCount, "本轮没处理成 ⇒ 计为未解决");
        Assert.IsFalse(result.ScanWatermarkAdvanced, "没有定论的路径必须让水位停住");
        Assert.IsTrue(
            (await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId)).Ledger.PendingRetries.ContainsKey(file),
            "必须记进持久待办：否则它既不在索引也不在重试表里，只能等下一次提示（复核 MAJOR-1）");
    }

    [TestMethod]
    public async Task APathThatBecomesNotApplicableClearsItsPendingRetry()
    {
        var file = WriteFile("A.cs", "class A { }");
        var (coordinator, updater) = Arrange();

        // 第一轮：语言侧报待重试 ⇒ 记退避、水位停住。
        updater.RetryablePaths.Add(file);
        var first = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(true, file));
        Assert.AreEqual(1, first.RetryableFileCount);
        Assert.IsTrue((await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId)).Ledger.PendingRetries.ContainsKey(file));

        // 第二轮（推过退避窗口）：语言侧改为「不归我管」⇒ 该路径已被处理，退避必须清掉。
        _now = _now.AddMinutes(2);
        updater.RetryablePaths.Clear();
        updater.NotApplicablePaths.Add(file);

        var second = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(true, file));

        Assert.AreEqual(1, second.NotApplicableFileCount);
        var snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.IsFalse(
            snapshot.Ledger.PendingRetries.ContainsKey(file),
            "走过 NotApplicable 的路径也要清退避，否则它会永久冻结水位");
        Assert.IsTrue(snapshot.Manifest.ContainsKey(file), "指纹仍要如实落地");
    }

    [TestMethod]
    public async Task DeletingAFileSchedulesItsDependentsForReindexing()
    {
        var owner = WriteFile("Owner.cs", "class Owner { }");
        var dependent = WriteFile("Dependent.cs", "class Dependent { }");

        var (coordinator, updater) = Arrange();

        // 先让两个文件都被索引（依赖方引用 Owner 的符号）。
        updater.ReferencesByPath[dependent] = [$"sym:{owner}"];
        await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(true, owner, dependent));
        Assert.IsTrue((await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId)).Manifest.ContainsKey(owner));

        // Owner 从磁盘消失 + 完整扫描 ⇒ 确认删除；依赖方必须被排进待办（它的入边刚被删掉）。
        File.Delete(owner);
        _now = _now.AddMinutes(1);

        var deletion = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(targeted: false));

        Assert.AreEqual(1, deletion.DeletedFileCount);
        CollectionAssert.Contains(
            deletion.InvalidatedDependentFilePaths.ToArray(),
            dependent,
            "删除会连带删掉其他文件的入边 ⇒ 必须把依赖方报出来");
        Assert.IsTrue(
            (await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId)).Ledger.PendingRetries.ContainsKey(dependent),
            "依赖方还要被排进持久待办，否则没人会去补它");
    }

    [TestMethod]
    public async Task AFileChangedDuringExtractionIsNotCommitted()
    {
        var file = WriteFile("A.cs", "class A { }");
        var (coordinator, updater) = Arrange();

        // 提取期间文件被改写（原子保存/另一个进程写入）：读到的指纹与提取出的结果不再对应同一份字节。
        updater.TouchFileDuringExtraction = path => File.AppendAllText(path, "\n// changed during extraction");

        var result = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(true, file));

        Assert.AreEqual(0, result.ExtractedFileCount, "错配的内容绝不能提交");
        Assert.AreEqual(1, result.RetryableFileCount);
        Assert.IsFalse(result.ScanWatermarkAdvanced);

        var snapshot = await _store.LoadSourceMaintenanceAsync(WorkspaceId, ScopeId);
        Assert.IsFalse(snapshot.Manifest.ContainsKey(file), "不写指纹");
        Assert.IsEmpty(await _store.GetSymbolsByFileAsync(WorkspaceId, ScopeId, file), "不写索引");
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

    /// <summary>真实枚举 + 可指定「元数据读不到」的路径（模拟 stat 失败）。</summary>
    private sealed class TreeScanner : ICodeSourceScanner, ICodeSourcePathProbe
    {
        private readonly bool _permissive;
        private readonly HashSet<string> _unreadable;

        public TreeScanner(bool permissive, string[]? unreadablePaths = null)
        {
            _permissive = permissive;
            _unreadable = new HashSet<string>(unreadablePaths ?? [], CodePathIdentity.PathComparer);
        }

        public Task<CodeSourceScanOutcome> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            _ = _permissive;

            var entries = Directory.Exists(rootPath)
                ? Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path =>
                    {
                        if (_unreadable.Contains(path))
                            return new CodeSourceDiskEntry(path, null, null);

                        var info = new FileInfo(path);
                        return new CodeSourceDiskEntry(
                            path, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
                    })
                    .ToArray()
                : [];

            return Task.FromResult(new CodeSourceScanOutcome(entries, RootUsable: true, Complete: true, null));
        }

        /// <summary>按路径观测：只给这些路径取元数据，结果必然不完整（因此永远不能确认删除）。</summary>
        public Task<CodeSourceScanOutcome> ObserveAsync(
            IReadOnlyCollection<string> absolutePaths,
            CancellationToken cancellationToken = default)
        {
            var entries = absolutePaths
                .Select(path =>
                {
                    if (_unreadable.Contains(path) || !File.Exists(path))
                        return new CodeSourceDiskEntry(path, null, null);

                    var info = new FileInfo(path);
                    return new CodeSourceDiskEntry(
                        path, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
                })
                .ToArray();

            return Task.FromResult(new CodeSourceScanOutcome(entries, RootUsable: true, Complete: false, null));
        }
    }

    /// <summary>语言侧替身：可脚本化待重试/不适用、可注入引用、可在提取时改动文件。</summary>
    private sealed class ScriptedUpdater : ICodeIndexFileBatchUpdater
    {
        public int CallCount { get; private set; }

        public void Reset() => CallCount = 0;

        public HashSet<string> RetryablePaths { get; } = new(CodePathIdentity.PathComparer);

        public HashSet<string> NotApplicablePaths { get; } = new(CodePathIdentity.PathComparer);

        public Dictionary<string, string[]> ReferencesByPath { get; } = new(CodePathIdentity.PathComparer);

        public Action<string>? TouchFileDuringExtraction { get; set; }

        public Task<CodeIndexFileBatchResult> UpdateFilesAsync(
            CodeWorkspaceDescriptor workspace,
            IReadOnlyCollection<string> filePaths,
            CodeIndexBatchContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            var outcomes = new List<CodeFileIndexOutcome>();

            foreach (var path in filePaths)
            {
                if (NotApplicablePaths.Contains(path))
                {
                    outcomes.Add(new CodeFileIndexOutcome(path, CodeIndexConsumerStatus.NotApplicable, Reason: "not ours"));
                    continue;
                }

                if (RetryablePaths.Contains(path))
                {
                    outcomes.Add(new CodeFileIndexOutcome(path, CodeIndexConsumerStatus.Retryable, Reason: "scripted"));
                    continue;
                }

                TouchFileDuringExtraction?.Invoke(path);

                var symbols = new List<CodeSymbolRecord>
                {
                    new(workspace.WorkspaceId, workspace.ProjectId, path, $"sym:{path}", "Symbol",
                        CodeSymbolKind.Class, 1, 2, "class Symbol", null),
                };

                var references = ReferencesByPath.TryGetValue(path, out var targets)
                    ? targets
                        .Select(target => new CodeReferenceRecord(
                            workspace.WorkspaceId,
                            workspace.ProjectId,
                            $"sym:{path}",
                            target,
                            path,
                            1))
                        .ToArray()
                    : [];

                outcomes.Add(new CodeFileIndexOutcome(
                    path,
                    CodeIndexConsumerStatus.Applied,
                    new CodeFileIndexPayload(path, symbols, references, [], "C#")));
            }

            return Task.FromResult(new CodeIndexFileBatchResult(
                outcomes, context.ConfigurationFingerprint, "review-session"));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly Func<DateTimeOffset> _now;

        public FixedTimeProvider(Func<DateTimeOffset> now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now();
    }
}
