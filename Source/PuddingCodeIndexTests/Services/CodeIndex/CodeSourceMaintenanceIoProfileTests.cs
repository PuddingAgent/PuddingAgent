using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// D4 磁盘 I/O 画像门禁（2026-10-02）：用可计数的装饰器**量**新链路读多少盘，而不是靠断言「应该很快」。
/// <para>
/// 被量化的三件事（正是诊断报告关心的放大）：
/// <list type="number">
///   <item><description>单文件改动 + 提示 ⇒ **零次全树枚举**、只读那一个文件的内容；</description></item>
///   <item><description>周期性完整校准 + 指纹全未变 ⇒ 有枚举但**零次内容读**；</description></item>
///   <item><description>完整校准 + 一个文件变了 ⇒ 只读那一个文件的内容（其余凭指纹跳过）。</description></item>
/// </list>
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSourceMaintenanceIoProfileTests : IDisposable
{
    private const string WorkspaceId = "ws-io";
    private const string ScopeId = "scope-io";

    private string _root = null!;
    private string _scopeRoot = null!;
    private SqliteCodeIndexStore _store = null!;
    private DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(1);

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-io-profile-tests", Guid.NewGuid().ToString("N"));
        _scopeRoot = Path.Combine(_root, "repo", "src");
        Directory.CreateDirectory(_scopeRoot);
        _store = new SqliteCodeIndexStore(Path.Combine(_root, "db", "code-index.db"));
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_scopeRoot, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private (CodeSourceMaintenanceCoordinator Coordinator, CountingScanner Scanner, CountingReader Reader) Arrange()
    {
        var scanner = new CountingScanner(new FileSystemCodeSourceScanner(new PermissiveIgnoreRules()));
        var reader = new CountingReader();
        var scanService = new CodeSourceScanService(
            scanner, _store, new FixedTimeProvider(() => _now), logger: NullLogger<CodeSourceScanService>.Instance);

        var coordinator = new CodeSourceMaintenanceCoordinator(
            scanService,
            _store,
            _store,
            new ScriptedBatchUpdater(),
            _store,
            reader,
            new FixedTimeProvider(() => _now),
            NullLogger<CodeSourceMaintenanceCoordinator>.Instance);

        return (coordinator, scanner, reader);
    }

    private CodeSourceMaintenanceRunOptions Options(bool targeted, params string[] hints) =>
        new(
            [new CodeConsumerInputFingerprint("C#", "policy-1", "semantic-1")],
            ProjectFilePaths: [],
            WatcherHints: hints.Length == 0 ? null : hints,
            Targeted: targeted);

    [TestMethod]
    public async Task ASingleFileChangeWalksNoTreeAndReadsExactlyThatFile()
    {
        var changed = WriteFile("A.cs", "class A { }");
        WriteFile("B.cs", "class B { }");
        WriteFile("nested/C.cs", "class C { }");

        var (coordinator, scanner, reader) = Arrange();
        var result = await coordinator.RunAsync(
            WorkspaceId, ScopeId, _scopeRoot, Options(targeted: true, changed));

        Assert.AreEqual(1, result.ExtractedFileCount);
        Assert.AreEqual(0, scanner.FullScanCount, "提示驱动的批次绝不能遍历整棵树");
        Assert.AreEqual(1, scanner.ProbeCount);
        Assert.AreEqual(1, reader.ReadCount, "只应读被改动的那一个文件的内容（其余靠指纹跳过）");
        Assert.AreEqual(0, result.RetryableFileCount);
    }

    [TestMethod]
    public async Task APeriodicFullCalibrationReadsNoContentWhenEveryFingerprintMatches()
    {
        var first = WriteFile("A.cs", "class A { }");
        var second = WriteFile("B.cs", "class B { }");

        var (coordinator, scanner, reader) = Arrange();

        // 第一轮：两个文件都新 ⇒ 各读一次内容并提交指纹。
        var initial = await coordinator.RunAsync(
            WorkspaceId, ScopeId, _scopeRoot, Options(targeted: true, first, second));
        Assert.AreEqual(2, initial.ExtractedFileCount);
        Assert.AreEqual(2, reader.ReadCount);

        scanner.Reset();
        reader.Reset();

        // 第二轮：周期性完整校准，磁盘没变 ⇒ 枚举一次，但**不读任何正文**。
        var calibration = await coordinator.RunAsync(
            WorkspaceId, ScopeId, _scopeRoot, Options(targeted: false));

        Assert.AreEqual(1, scanner.FullScanCount, "周期校准必须完整枚举");
        Assert.AreEqual(0, scanner.ProbeCount);
        Assert.AreEqual(0, reader.ReadCount, "指纹全未变 ⇒ 一次正文都不该读（这正是旧路径的放大来源）");
        Assert.AreEqual(0, calibration.ExtractedFileCount);
    }

    [TestMethod]
    public async Task AFullCalibrationReadsOnlyTheFileThatActuallyChanged()
    {
        var first = WriteFile("A.cs", "class A { }");
        var second = WriteFile("B.cs", "class B { }");

        var (coordinator, scanner, reader) = Arrange();
        await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(targeted: true, first, second));

        scanner.Reset();
        reader.Reset();

        // 只改一个文件的正文（并把长度/时间真正改掉）。
        await File.WriteAllTextAsync(first, "class A { int Extra; }");

        var calibration = await coordinator.RunAsync(
            WorkspaceId, ScopeId, _scopeRoot, Options(targeted: false));

        Assert.AreEqual(1, scanner.FullScanCount);
        Assert.AreEqual(1, reader.ReadCount, "只有真正变了的那一个文件需要读正文");
        Assert.AreEqual(1, calibration.ExtractedFileCount);
    }

    [TestMethod]
    public async Task ASecondTargetedRunForAnUnchangedHintReadsOnceAndSkipsExtraction()
    {
        var file = WriteFile("A.cs", "class A { }");

        var (coordinator, scanner, reader) = Arrange();
        await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(targeted: true, file));
        Assert.AreEqual(1, reader.ReadCount);

        scanner.Reset();
        reader.Reset();

        // 同一个提示再来一次（例如编辑器抖动/重复事件）：
        // 提示 ⇒ 必须核验内容（1 次读盘），但指纹一致 ⇒ **不该再惊动语言侧**。
        var again = await coordinator.RunAsync(WorkspaceId, ScopeId, _scopeRoot, Options(targeted: true, file));

        Assert.AreEqual(0, scanner.FullScanCount, "仍然不许遍历整棵树");
        Assert.AreEqual(1, reader.ReadCount, "提示要求核验内容：这次读盘是设计内的代价");
        Assert.AreEqual(1, again.ReusedFileCount, "指纹一致 ⇒ 只刷新消费者视图");
        Assert.AreEqual(0, again.ExtractedFileCount, "内容没变就不该启动提取器（一次提取远贵于一次读盘）");
        Assert.AreEqual(0, again.RetryableFileCount);
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

    /// <summary>计数装饰器：分别记录「全树枚举」与「按路径观测」的次数，并转发给真实扫描器。</summary>
    private sealed class CountingScanner : ICodeSourceScanner, ICodeSourcePathProbe
    {
        private readonly FileSystemCodeSourceScanner _inner;

        public CountingScanner(FileSystemCodeSourceScanner inner) => _inner = inner;

        public int FullScanCount { get; private set; }

        public int ProbeCount { get; private set; }

        public void Reset()
        {
            FullScanCount = 0;
            ProbeCount = 0;
        }

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

    /// <summary>计数稳定读取次数（真实实现，只多一个计数器）。</summary>
    private sealed class CountingReader : CodeSourceFingerprintReader
    {
        public int ReadCount { get; private set; }

        public void Reset() => ReadCount = 0;

        public override Task<CodeSourceReadResult> ReadAsync(
            string filePath, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(filePath, cancellationToken);
        }
    }

    private sealed class ScriptedBatchUpdater : ICodeIndexFileBatchUpdater
    {
        public Task<CodeIndexFileBatchResult> UpdateFilesAsync(
            CodeWorkspaceDescriptor workspace,
            IReadOnlyCollection<string> filePaths,
            CodeIndexBatchContext context,
            CancellationToken cancellationToken = default)
        {
            var outcomes = filePaths
                .Select(path => new CodeFileIndexOutcome(
                    path,
                    CodeIndexConsumerStatus.Applied,
                    new CodeFileIndexPayload(
                        path,
                        [new CodeSymbolRecord(workspace.WorkspaceId, workspace.ProjectId, path, $"sym:{path}",
                            "Symbol", CodeSymbolKind.Class, 1, 2, "class Symbol", null)],
                        [],
                        [],
                        "C#")))
                .ToArray();

            return Task.FromResult(new CodeIndexFileBatchResult(
                outcomes, context.ConfigurationFingerprint, "io-profile-session"));
        }
    }

    private sealed class PermissiveIgnoreRules : ICodeSourceIgnoreRules
    {
        public bool IsIgnored(string absolutePath, bool isDirectory) => false;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly Func<DateTimeOffset> _now;

        public FixedTimeProvider(Func<DateTimeOffset> now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now();
    }
}
