using System.Collections;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Text;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A22b：扫描期逐文件隔离。
/// <para>
/// <b>缺陷本体（改动前）</b>：<c>LuceneSearchEngine.BuildIndexInternalAsync</c> 的扫描块把
/// <c>catch (DirectoryNotFoundException)</c> / <c>catch (UnauthorizedAccessException)</c> 挂在
/// <b>整轮枚举之外</b> —— 扫描途中任一个文件抛这两类异常，被丢弃的是<b>整轮枚举的剩余部分</b>
/// （生产实测形态：4510 个文件只收集到 99 个，另一轮 0 个），而构建照常返回 <c>Success=true</c>。
/// </para>
/// <para>
/// 本文件锁定三件事：① 判定体（<see cref="FileCandidateCollector"/>）在单文件粒度上可单测且语义逐条不变；
/// ② 取消原样外抛，绝不被"坏文件"的吞异常吞掉；③ 跳过数对外可见
/// （<see cref="FullTextIndexResult.SkippedByError"/>），且非取消异常变成<b>明确失败</b>而不是静默丢弃。
/// </para>
/// <para>
/// 所有测试的语料与索引根都落在 <c>%TEMP%</c> 下（见 <see cref="Initialize"/> 的硬断言），
/// 结构上碰不到 <c>D:\data\fulltext-index</c>。
/// </para>
/// </summary>
[TestClass]
public sealed class ScanPerFileIsolationTests
{
    private string _root = null!;
    private string _scanRoot = null!;
    private string _indexRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-a22b-" + Guid.NewGuid().ToString("N"));

        // 硬断言：语料与索引只允许落在 %TEMP% 下
        Assert.IsTrue(
            _root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"测试根必须在 %TEMP% 下，实际 {_root}");

        _scanRoot = Path.Combine(_root, "corpus");
        _indexRoot = Path.Combine(_root, "index");
        Directory.CreateDirectory(_scanRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应把测试判红
        }
    }

    // ── A1：helper 正常文件 ──────────────────────────────────────────

    [TestMethod]
    public void A1_TryCollectCandidate_AcceptsNormalFile_WithDiskFacts()
    {
        var file = Write("Alpha.cs", "class Alpha { const string Marker = \"A22bAlpha\"; }");
        var options = Options();

        var accepted = FileCandidateCollector.TryCollectCandidate(
            file, _scanRoot, options, null, out var entry, out var reason);

        Assert.IsTrue(accepted, "正常 .cs 文件必须被接受");
        Assert.IsNull(reason, "接受时不得给出跳过原因");
        Assert.AreEqual(file, entry.Path, "条目路径必须沿用枚举器给出的原始路径（不做规范化）");

        var fi = new FileInfo(file);
        Assert.AreEqual(fi.Length, entry.Size, "条目字节数必须与磁盘一致");
        Assert.AreEqual(fi.LastWriteTimeUtc, entry.LastWrite, "条目时间戳必须与磁盘一致");
        Assert.IsFalse(FileCandidateCollector.IsErrorSkip(reason), "被接受的文件不是「异常跳过」");
    }

    // ── A2：helper 文件在枚举后消失 ⇒ 只跳过它自己 ──────────────────

    [TestMethod]
    public void A2_TryCollectCandidate_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var missing = Path.Combine(_scanRoot, "ghost.cs");
        var options = Options();

        var accepted = FileCandidateCollector.TryCollectCandidate(
            missing, _scanRoot, options, null, out var entry, out var reason);

        Assert.IsFalse(accepted, "文件在枚举后消失 ⇒ 只跳过它自己（旧实现会连带丢弃整轮枚举）");
        Assert.IsNotNull(reason, "异常跳过必须给出原因");
        StringAssert.StartsWith(reason, CandidateSkipReasons.ErrorPrefix, "异常原因必须带 error: 前缀");
        StringAssert.Contains(reason!, nameof(FileNotFoundException), "原因必须点名真实异常类型");
        Assert.IsTrue(
            FileCandidateCollector.IsErrorSkip(reason),
            "异常跳过必须能被调用方识别（否则不会计入 SkippedByError）");
        Assert.AreEqual(default(CandidateEntry), entry, "被跳过时不得给出候选条目");
    }

    // ── A3：helper 策略性跳过逐条保持既有语义 ──────────────────────

    [TestMethod]
    public void A3_TryCollectCandidate_PolicySkips_KeepTheirOwnReasons()
    {
        var options = Options(maxFileSizeBytes: 1024);
        var ok = Write("Ok.cs", "class Ok { }");

        AssertSkip(Write("binary.bin", "A22b 不可索引扩展名"), options, null,
            CandidateSkipReasons.NotIndexableExtension);

        AssertSkip(Write("note.md", "A22b pattern 不匹配"), options, new[] { "*.cs" },
            CandidateSkipReasons.PatternMismatch);

        AssertSkip(Write(Path.Combine("bin", "Dropped.cs"), "class Dropped { }"), options, null,
            CandidateSkipReasons.ExcludedPath);

        AssertSkip(Write("Empty.cs", string.Empty), options, null,
            CandidateSkipReasons.EmptyFile);

        AssertSkip(Write("Huge.cs", new string('x', 4096)), options, null,
            CandidateSkipReasons.TooLarge);

        // 判定顺序与改动前一致：先「被排除路径」后「体积」（同一文件同命中两条时的既有取向）
        AssertSkip(Write(Path.Combine("bin", "HugeToo.cs"), new string('x', 4096)), options, null,
            CandidateSkipReasons.ExcludedPath);

        // 对照组：同一 helper 仍然接受正常文件（证明上面的 false 不是"任何入参都 false"）
        Assert.IsTrue(
            FileCandidateCollector.TryCollectCandidate(ok, _scanRoot, options, new[] { "*.cs" }, out _, out _),
            "正常文件必须仍被接受");
    }

    // ── A4：helper 取消必须原样外抛 ─────────────────────────────────

    [TestMethod]
    public void A4_TryCollectCandidate_CancelledToken_ThrowsOperationCanceled()
    {
        var file = Write("Cancel.cs", "class Cancel { }");
        var options = Options();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            FileCandidateCollector.TryCollectCandidate(file, _scanRoot, options, null, cts.Token, out _, out _));

        // 对照组：未取消时同一入口正常返回
        Assert.IsTrue(
            FileCandidateCollector.TryCollectCandidate(file, _scanRoot, options, null, CancellationToken.None, out _, out _),
            "未取消时同一入口必须正常接受（证明上面的红不是入口恒抛）");
    }

    // ── A5：真 Lucene · 300 文件全部入库 ───────────────────────────

    [TestMethod]
    public async Task A5_RealLucene_ThreeHundredFiles_AllIndexed_WithNoSkips()
    {
        const int fileCount = 300;
        for (var i = 0; i < fileCount; i++)
            Write($"File{i:D3}.cs", $"class File{i:D3} {{ const string Marker = \"A22bMarker{i:D3}\"; }}");

        using var engine = new LuceneSearchEngine(new FullTextIndexOptions { IndexRootDirectory = _indexRoot });
        var result = await engine.BuildIndexAsync(_scanRoot);

        Assert.IsTrue(result.Success, "构建必须成功：" + result.Error);
        Assert.AreEqual(fileCount, result.IndexedFileCount, "300 个文件必须全部入库");
        Assert.AreEqual(0, result.SkippedByError, "没有坏文件 ⇒ 跳过数必须为 0");
        Assert.IsNull(result.Error, "无跳过时终态不应带错误文本");
    }

    // ── A6：真 Lucene · 写入期跳过对外可见 ─────────────────────────

    [TestMethod]
    public async Task A6_RealLucene_WritePhaseSkip_IsVisible()
    {
        const int healthy = 20;
        for (var i = 0; i < healthy; i++)
            Write($"Write{i:D2}.cs", $"class Write{i:D2} {{ const string Marker = \"A22bWrite{i:D2}\"; }}");
        Write("Boom.bomb", "A22b：抽取期抛 IOException 的语料");

        var options = new FullTextIndexOptions
        {
            IndexRootDirectory = _indexRoot,
            ParsedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bomb" },
        };

        using var engine = new LuceneSearchEngine(
            options, new JiebaAnalyzer(), new IFileContentExtractor[] { new ThrowingExtractor(".bomb") });
        var result = await engine.BuildIndexAsync(_scanRoot);

        Assert.IsTrue(result.Success, "坏文件只应被跳过，不应把构建打成失败：" + result.Error);
        Assert.AreEqual(healthy, result.IndexedFileCount, "坏文件之外的其余文件必须全部入库");
        Assert.IsTrue(
            result.SkippedByError >= 1,
            $"写入期被跳过的文件必须对外可见（实际 {result.SkippedByError}）");
        Assert.IsNotNull(result.Error, "有跳过时终态不得表现成「一切正常」");
    }

    // ── A7：真 Lucene · 扫描期未预期异常 ⇒ 明确失败（带计数）────────

    [TestMethod]
    public async Task A7_RealLucene_UnexpectedScanException_FailsLoudlyWithCounts()
    {
        Write("First.cs", "class First { }");
        Write("Second.cs", "class Second { }");

        // 注入点：扩展名白名单集合在第 2 次 Contains 调用时抛 InvalidOperationException。
        // 它不是文件系统异常 ⇒ 不是「坏文件」，因此必须变成明确失败（旧实现返回 Success=false 但计数被硬写成 0）。
        var options = new FullTextIndexOptions
        {
            IndexRootDirectory = _indexRoot,
            PlainTextExtensions = new ThrowingContainsSet(
                new[] { ".cs" }, throwAtCall: 2, new InvalidOperationException("A22b 注入：扫描期未预期异常")),
        };

        using var engine = new LuceneSearchEngine(options);
        var result = await engine.BuildIndexAsync(_scanRoot);

        Assert.IsFalse(result.Success, "非取消异常必须明确失败（不得静默丢弃成部分成功）");
        Assert.AreEqual(1, result.IndexedFileCount, "计数必须是「已经走到哪」的事实，而不是硬写的 0");
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "扫描被中断", "消息必须明确「扫描被中断」");
        StringAssert.Contains(result.Error, nameof(InvalidOperationException), "消息必须点名异常类型");
        StringAssert.Contains(result.Error, "已收集文件 1 个", "消息必须带上已收集文件数");
    }

    // ── A8：真 Lucene · 扫描期单个文件 IO 异常 ⇒ 只跳过该文件 ────────

    [TestMethod]
    public async Task A8_RealLucene_PerFileScanIOException_SkipsOnlyThatOneFile()
    {
        const int total = 12;
        for (var i = 0; i < total; i++)
            Write($"Iso{i:D2}.cs", $"class Iso{i:D2} {{ const string Marker = \"A22bIso{i:D2}\"; }}");

        // 注入点：扩展名白名单集合在第 3 次 Contains 调用时抛 IOException ——
        // 这正是旧实现会「吞掉整轮枚举」的异常类别；在逐文件粒度上它只应表现为「少一个候选 + 一次计数」。
        var options = new FullTextIndexOptions
        {
            IndexRootDirectory = _indexRoot,
            PlainTextExtensions = new ThrowingContainsSet(
                new[] { ".cs" }, throwAtCall: 3, new IOException("A22b 注入：单文件 IO 失败")),
        };

        using var engine = new LuceneSearchEngine(options);
        var result = await engine.BuildIndexAsync(_scanRoot);

        Assert.IsTrue(result.Success, "单个坏文件不得把整轮构建打成失败：" + result.Error);
        Assert.AreEqual(total - 1, result.IndexedFileCount, "只有那一个文件被跳过，其余必须全部入库");
        Assert.AreEqual(1, result.SkippedByError, "被跳过的文件必须计数并对外可见");
    }

    // ── 测试替身与工具 ─────────────────────────────────────────────

    private FullTextIndexOptions Options(long maxFileSizeBytes = 10 * 1024 * 1024) =>
        new() { IndexRootDirectory = _indexRoot, MaxFileSizeBytes = maxFileSizeBytes };

    private void AssertSkip(string file, FullTextIndexOptions options, string[]? patterns, string expectedReason)
    {
        var accepted = FileCandidateCollector.TryCollectCandidate(
            file, _scanRoot, options, patterns, out var entry, out var reason);

        Assert.IsFalse(accepted, $"'{Path.GetFileName(file)}' 必须被拒绝");
        Assert.AreEqual(expectedReason, reason, $"'{Path.GetFileName(file)}' 的跳过原因必须保持既有语义");
        Assert.IsFalse(
            FileCandidateCollector.IsErrorSkip(reason),
            "策略性跳过不是「因异常跳过」，不得计入 SkippedByError");
        Assert.AreEqual(default(CandidateEntry), entry, "被拒绝时不得给出候选条目");
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_scanRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>抽取期固定抛 <see cref="IOException"/> 的提取器（写入期跳过的注入点）。</summary>
    private sealed class ThrowingExtractor : IFileContentExtractor
    {
        public ThrowingExtractor(params string[] extensions) =>
            SupportedExtensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> SupportedExtensions { get; }

        public Task<string> ExtractAsync(string filePath, CancellationToken ct = default) =>
            Task.FromException<string>(new IOException($"A22b 注入：抽取 {Path.GetFileName(filePath)} 失败"));
    }

    /// <summary>
    /// 可注入「第 N 次 <see cref="Contains"/> 抛指定异常」的扩展名白名单集合 —— 用它把
    /// 「扫描期单文件异常」变成<b>确定性</b>输入，无需给生产代码开注入口。
    /// </summary>
    private sealed class ThrowingContainsSet : IReadOnlySet<string>
    {
        private readonly HashSet<string> _inner;
        private readonly int _throwAtCall;
        private readonly Exception _error;
        private int _calls;

        public ThrowingContainsSet(IEnumerable<string> values, int throwAtCall, Exception error)
        {
            _inner = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            _throwAtCall = throwAtCall;
            _error = error;
        }

        public int Count => _inner.Count;

        public bool Contains(string item)
        {
            _calls++;
            if (_calls == _throwAtCall)
                throw _error;

            return _inner.Contains(item);
        }

        public IEnumerator<string> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<string> other) => _inner.IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<string> other) => _inner.IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<string> other) => _inner.IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<string> other) => _inner.IsSupersetOf(other);

        public bool Overlaps(IEnumerable<string> other) => _inner.Overlaps(other);

        public bool SetEquals(IEnumerable<string> other) => _inner.SetEquals(other);
    }
}
