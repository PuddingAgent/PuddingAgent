using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Text;

namespace PuddingFullTextIndexTests;

[TestClass]
public sealed class JiebaAnalyzerTests
{
    [TestMethod]
    public void Tokenizes_Chinese_Text()
    {
        using var analyzer = new JiebaAnalyzer();
        var tokens = Tokenize(analyzer, "content", "我爱北京天安门");

        // CJKBigramFilter produces overlapping 2-grams
        Assert.IsTrue(tokens.Count >= 3, $"Expected at least 3 bigram tokens, got {tokens.Count}: [{string.Join(", ", tokens)}]");
        CollectionAssert.Contains(tokens, "我爱");
        CollectionAssert.Contains(tokens, "北京");
    }

    [TestMethod]
    public void Tokenizes_Mixed_Chinese_And_English()
    {
        using var analyzer = new JiebaAnalyzer();
        var tokens = Tokenize(analyzer, "content", "Pudding 是一个智能体平台");

        Assert.IsTrue(tokens.Contains("pudding"), "Should contain 'pudding' (lowercased)");
    }

    [TestMethod]
    public void Tokenizes_Empty_Text()
    {
        using var analyzer = new JiebaAnalyzer();
        var tokens = Tokenize(analyzer, "content", "");

        Assert.AreEqual(0, tokens.Count);
    }

    [TestMethod]
    public void Tokenizes_English_Only()
    {
        using var analyzer = new JiebaAnalyzer();
        var tokens = Tokenize(analyzer, "content", "Hello World");

        Assert.IsTrue(tokens.Contains("hello"), "Should contain 'hello' (lowercased)");
        Assert.IsTrue(tokens.Contains("world"), "Should contain 'world' (lowercased)");
    }

    private static List<string> Tokenize(Analyzer analyzer, string fieldName, string text)
    {
        var tokens = new List<string>();
        using var stream = analyzer.GetTokenStream(fieldName, new System.IO.StringReader(text));
        stream.Reset();

        var termAttr = stream.GetAttribute<ICharTermAttribute>();
        while (stream.IncrementToken())
        {
            tokens.Add(termAttr.ToString());
        }

        return tokens;
    }
}

[TestClass]
public sealed class FullTextIndexOptionsTests
{
    [TestMethod]
    public void PlainTextExtensions_Contains_Common_Code_Extensions()
    {
        var options = new FullTextIndexOptions();

        Assert.IsTrue(options.PlainTextExtensions.Contains(".cs"));
        Assert.IsTrue(options.PlainTextExtensions.Contains(".ts"));
        Assert.IsTrue(options.PlainTextExtensions.Contains(".py"));
        Assert.IsTrue(options.PlainTextExtensions.Contains(".md"));
        Assert.IsTrue(options.PlainTextExtensions.Contains(".json"));
        Assert.IsTrue(options.PlainTextExtensions.Contains(".html"));
    }

    [TestMethod]
    public void IsIndexableExtension_Returns_True_For_Whitelisted()
    {
        var options = new FullTextIndexOptions();

        Assert.IsTrue(options.IsIndexableExtension(".cs"));
        Assert.IsTrue(options.IsIndexableExtension(".md"));
    }

    [TestMethod]
    public void IsIndexableExtension_Returns_False_For_Not_Whitelisted()
    {
        var options = new FullTextIndexOptions();

        Assert.IsFalse(options.IsIndexableExtension(".dll"));
        Assert.IsFalse(options.IsIndexableExtension(".exe"));
        Assert.IsFalse(options.IsIndexableExtension(".png"));
    }

    [TestMethod]
    public void IsIndexableExtension_Is_Case_Insensitive()
    {
        var options = new FullTextIndexOptions();

        Assert.IsTrue(options.IsIndexableExtension(".CS"));
        Assert.IsTrue(options.IsIndexableExtension(".Md"));
        Assert.IsTrue(options.IsIndexableExtension(".JSON"));
    }

    [TestMethod]
    public void IsIndexableExtension_Returns_True_For_Parsed_Extensions()
    {
        var options = new FullTextIndexOptions
        {
            ParsedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx" },
        };

        Assert.IsTrue(options.IsIndexableExtension(".pdf"));
        Assert.IsTrue(options.IsIndexableExtension(".docx"));
    }

    [TestMethod]
    public void IndexRootDirectory_Uses_PUDDING_DATA_ROOT_When_Set()
    {
        var oldValue = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("PUDDING_DATA_ROOT", @"C:\test-data");
            var options = new FullTextIndexOptions();

            StringAssert.Contains(options.IndexRootDirectory, @"C:\test-data");
            StringAssert.Contains(options.IndexRootDirectory, "fulltext-index");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PUDDING_DATA_ROOT", oldValue);
        }
    }
}

[TestClass]
public sealed class PlainTextExtractorTests
{
    [TestMethod]
    public async Task ExtractAsync_Reads_Text_File()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "Hello, 世界!");

            var extractor = new PlainTextExtractor();
            var content = await extractor.ExtractAsync(tempFile);

            Assert.AreEqual("Hello, 世界!", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void SupportedExtensions_Contains_Code_Extensions()
    {
        var extractor = new PlainTextExtractor();

        Assert.IsTrue(extractor.SupportedExtensions.Contains(".cs"));
        Assert.IsTrue(extractor.SupportedExtensions.Contains(".py"));
        Assert.IsTrue(extractor.SupportedExtensions.Contains(".md"));
    }
}

[TestClass]
public sealed class LuceneSearchEngineTests
{
    private string _tempDataDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"pudding-ft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDataDir))
            Directory.Delete(_tempDataDir, recursive: true);
    }

    [TestMethod]
    public void HasIndex_Returns_False_When_Not_Indexed()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();

        Assert.IsFalse(engine.HasIndex(dir));
    }

    [TestMethod]
    public async Task BuildIndex_And_HasIndex_Returns_True()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();

        var result = await engine.BuildIndexAsync(dir);
        Assert.IsTrue(result.Success, $"BuildIndex failed: {result.Error}");

        Assert.IsTrue(engine.HasIndex(dir));
    }

    [TestMethod]
    public async Task BuildIndex_Indexes_Correct_File_Count()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();

        var result = await engine.BuildIndexAsync(dir);

        Assert.IsTrue(result.Success);
        // 2 .cs files + 1 .md file = 3 files
        Assert.AreEqual(3, result.IndexedFileCount, $"Expected 3 files, got {result.IndexedFileCount}");
    }

    [TestMethod]
    public async Task BuildIndex_Skips_Non_Whitelisted_Files()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();
        // add a .dll file
        File.WriteAllText(Path.Combine(dir, "test.dll"), "should be skipped");

        var result = await engine.BuildIndexAsync(dir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.IndexedFileCount, "Should only index 3 whitelisted files, not .dll");
    }

    [TestMethod]
    public async Task Search_Finds_Text_In_Indexed_Files()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();
        await engine.BuildIndexAsync(dir);

        // search for "Needle" which appears in Program.cs
        var result = await engine.SearchAsync("Needle", dir);

        Assert.IsTrue(result.Success, $"Search failed: {result.Error}");
        Assert.IsTrue(result.Matches.Count >= 1, "Should find at least 1 match for 'Needle'");
        Assert.IsTrue(result.Matches.Any(m => m.FilePath.Contains("Program.cs")));
    }

    [TestMethod]
    public async Task Search_Finds_Chinese_Text()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();
        await engine.BuildIndexAsync(dir);

        // search for Chinese text in readme.md
        var result = await engine.SearchAsync("布丁", dir);

        Assert.IsTrue(result.Success, $"Search failed: {result.Error}");
        Assert.IsTrue(result.Matches.Count >= 1, "Should find at least 1 match for '布丁'");
    }

    [TestMethod]
    public async Task Search_Returns_Match_With_Line_Number()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();
        await engine.BuildIndexAsync(dir);

        var result = await engine.SearchAsync("Needle", dir);

        Assert.IsTrue(result.Success);
        var match = result.Matches.First(m => m.FilePath.Contains("Program.cs"));
        Assert.IsTrue(match.LineNumber > 0, $"Line number should be > 0, got {match.LineNumber}");
    }

    [TestMethod]
    public async Task Search_Returns_Empty_For_Unindexed_Directory()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();

        var result = await engine.SearchAsync("anything", dir);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "not indexed");
    }

    [TestMethod]
    public async Task Search_Returns_No_Matches_For_Nonexistent_Text()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();
        await engine.BuildIndexAsync(dir);

        var result = await engine.SearchAsync("xyznonexistentpattern999", dir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Matches.Count);
    }

    [TestMethod]
    public void RemoveIndex_Clears_Index()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();

        engine.BuildIndexAsync(dir).Wait();
        Assert.IsTrue(engine.HasIndex(dir));

        var removed = engine.RemoveIndex(dir);
        Assert.IsTrue(removed);
        Assert.IsFalse(engine.HasIndex(dir));
    }

    [TestMethod]
    public void RemoveIndex_Returns_True_When_Not_Indexed()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();

        var removed = engine.RemoveIndex(dir);
        Assert.IsTrue(removed, "RemoveIndex should return true when directory was not indexed");
    }

    [TestMethod]
    public async Task BuildIndex_With_Custom_FilePatterns()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();
        // also add a .txt file that is normally whitelisted
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "some notes");

        // only index .cs files
        var result = await engine.BuildIndexAsync(dir, filePatterns: ".cs");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, result.IndexedFileCount, "Should only index 2 .cs files, not .md or .txt");
    }

    [TestMethod]
    public async Task Search_Returns_Elapsed_Ms()
    {
        using var engine = CreateEngine();
        var dir = CreateTestFiles();
        await engine.BuildIndexAsync(dir);

        var result = await engine.SearchAsync("Needle", dir);

        Assert.IsTrue(result.ElapsedMs >= 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private LuceneSearchEngine CreateEngine()
    {
        var options = new FullTextIndexOptions
        {
            IndexRootDirectory = _tempDataDir,
        };
        return new LuceneSearchEngine(options);
    }

    /// <summary>Create test files in a temp directory: 2 .cs, 1 .md.</summary>
    private string CreateTestFiles()
    {
        var dir = Path.Combine(_tempDataDir, "test-project");
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "Program.cs"),
            "using System;\n\nclass Program {\n    static void Main() {\n        var needle = \"Needle\";\n    }\n}");

        File.WriteAllText(Path.Combine(dir, "Calculator.cs"),
            "public class Calculator {\n    public int Add(int a, int b) => a + b;\n}");

        File.WriteAllText(Path.Combine(dir, "readme.md"),
            "# 布丁智能体\n\n这是一个全文搜索测试。\n\nPudding 是一个智能体平台。");

        return dir;
    }
}

// ══════════════════════════════════════════════════════════════════
// 集成测试：大海捞针 & 索引性能 & 文件更新 & 文档检索
// ══════════════════════════════════════════════════════════════════

[TestClass]
public sealed class FullTextIndexIntegrationTests
{
    private string _tempDataDir = null!;
    private string _workspaceDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"pudding-ft-int-{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(_tempDataDir, "workspace");
        Directory.CreateDirectory(_tempDataDir);
        Directory.CreateDirectory(_workspaceDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDataDir))
            Directory.Delete(_tempDataDir, recursive: true);
    }

    // ── 索引性能：在 PuddingAgent/Source/PuddingRuntime 上构建索引 ──

    [TestMethod]
    [Timeout(120_000)]
    public async Task Index_PuddingRuntime_Directory_Performance()
    {
        var runtimeDir = @"D:\WangXianQiang\github\hyfree\PuddingAgent\Source\PuddingRuntime";
        if (!Directory.Exists(runtimeDir))
        {
            Assert.Inconclusive($"Directory not found: {runtimeDir}");
            return;
        }

        using var engine = CreateEngine();

        var result = await engine.BuildIndexAsync(runtimeDir, filePatterns: ".cs;.csproj;.json;.config");

        Assert.IsTrue(result.Success, $"Index build failed: {result.Error}");
        Assert.IsTrue(result.IndexedFileCount > 0, "Should index at least some .cs files");
        Assert.IsTrue(result.ElapsedMs < 60_000, $"Indexing {result.IndexedFileCount} files took {result.ElapsedMs}ms, should be under 60s");
        Assert.IsTrue(result.TotalBytes > 0, "Should report total bytes");

        Console.WriteLine($"Indexed {result.IndexedFileCount} files, {result.TotalBytes} bytes in {result.ElapsedMs}ms");
        Console.WriteLine($"Throughput: {result.IndexedFileCount / (result.ElapsedMs / 1000.0):F1} files/s");
    }

    // ── 大海捞针：大量文件中搜索特定关键词 ──

    [TestMethod]
    [Timeout(120_000)]
    public async Task Needle_In_Haystack_Search_After_Index()
    {
        var runtimeDir = @"D:\WangXianQiang\github\hyfree\PuddingAgent\Source\PuddingRuntime";
        if (!Directory.Exists(runtimeDir))
        {
            Assert.Inconclusive($"Directory not found: {runtimeDir}");
            return;
        }

        using var engine = CreateEngine();

        // Build index first
        var buildResult = await engine.BuildIndexAsync(runtimeDir, filePatterns: ".cs");
        Assert.IsTrue(buildResult.Success, $"Build failed: {buildResult.Error}");

        // Needle: SearchGrepTool should appear in class definition
        var needle = "SearchGrepTool";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.SearchAsync(needle, runtimeDir, maxResults: 10);
        sw.Stop();

        Assert.IsTrue(result.Success, $"Search failed: {result.Error}");
        Assert.IsTrue(result.Matches.Count >= 1, $"Should find '{needle}' in SearchGrepTool.cs");
        Assert.IsTrue(result.Matches.Any(m => m.FilePath.Contains("SearchGrepTool.cs")),
            "Should match SearchGrepTool.cs specifically");
        Assert.IsTrue(result.ElapsedMs < 1000, $"Search should be sub-second, got {result.ElapsedMs}ms");

        Console.WriteLine($"Needle '{needle}' found {result.TotalMatches} matches (returned {result.Matches.Count}) in {result.ElapsedMs}ms");
        foreach (var m in result.Matches.Take(3))
            Console.WriteLine($"  {Path.GetFileName(m.FilePath)}:{m.LineNumber}: {m.LineText.Trim()[..Math.Min(60, m.LineText.Trim().Length)]}");
    }

    // ── 大海捞针 2：搜索中文热词 ──

    [TestMethod]
    [Timeout(120_000)]
    public async Task Needle_In_Haystack_Chinese_Search()
    {
        var runtimeDir = @"D:\WangXianQiang\github\hyfree\PuddingAgent\Source\PuddingRuntime";
        if (!Directory.Exists(runtimeDir))
        {
            Assert.Inconclusive($"Directory not found: {runtimeDir}");
            return;
        }

        using var engine = CreateEngine();
        var buildResult = await engine.BuildIndexAsync(runtimeDir, filePatterns: ".cs");
        Assert.IsTrue(buildResult.Success);

        // Chinese text in tool descriptions
        var result = await engine.SearchAsync("搜索", runtimeDir, maxResults: 10);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Matches.Count >= 1, "Should find Chinese '搜索' in tool descriptions/strings");
        Console.WriteLine($"Chinese '搜索' found {result.TotalMatches} matches in {result.ElapsedMs}ms");
    }

    // ── 文件更新 → 检测索引过期 → 重建索引 ──

    [TestMethod]
    public async Task File_Update_Rebuild_Index_Reflects_Changes()
    {
        using var engine = CreateEngine();

        // 创建初始文件并索引
        File.WriteAllText(Path.Combine(_workspaceDir, "doc.txt"), "cat dog");
        await engine.BuildIndexAsync(_workspaceDir);

        // 确认搜索 "dog" 能找到
        var r1 = await engine.SearchAsync("dog", _workspaceDir);
        Assert.IsTrue(r1.Matches.Count >= 1, "Should find 'dog' in initial index");

        // 更新文件内容
        File.WriteAllText(Path.Combine(_workspaceDir, "doc.txt"), "elephant giraffe");

        // 未重建索引时旧内容仍可搜到（Lucene 特性）
        // 重建索引后
        await engine.BuildIndexAsync(_workspaceDir);

        var r2 = await engine.SearchAsync("dog", _workspaceDir);
        Assert.AreEqual(0, r2.Matches.Count, "After rebuild, 'dog' should be gone");

        var r3 = await engine.SearchAsync("giraffe", _workspaceDir);
        Assert.IsTrue(r3.Matches.Count >= 1, "After rebuild, 'giraffe' should be found");
    }

    // ── 新增文件 → 重建索引可见 ──

    [TestMethod]
    public async Task New_File_Added_Rebuild_Index_Includes_It()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "a.txt"), "hello");
        await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(engine.HasIndex(_workspaceDir));
        var r1 = await engine.SearchAsync("hello", _workspaceDir);
        Assert.AreEqual(1, r1.Matches.Count);

        // 新增文件
        File.WriteAllText(Path.Combine(_workspaceDir, "b.txt"), "world");

        // 未重建：找不到 b.txt
        var r2 = await engine.SearchAsync("world", _workspaceDir);
        Assert.AreEqual(0, r2.Matches.Count, "Without rebuild, new file not indexed");

        // 重建
        await engine.BuildIndexAsync(_workspaceDir);
        var r3 = await engine.SearchAsync("world", _workspaceDir);
        Assert.AreEqual(1, r3.Matches.Count, "After rebuild, new file found");
    }

    // ── 删除文件 → 重建索引移除 ──

    [TestMethod]
    public async Task Deleted_File_Rebuild_Index_Removes_It()
    {
        using var engine = CreateEngine();

        var file1 = Path.Combine(_workspaceDir, "keep.txt");
        var file2 = Path.Combine(_workspaceDir, "remove.txt");
        File.WriteAllText(file1, "keep me");
        File.WriteAllText(file2, "remove me");
        await engine.BuildIndexAsync(_workspaceDir);

        // 删除文件
        File.Delete(file2);

        // 重建
        await engine.BuildIndexAsync(_workspaceDir);

        var r1 = await engine.SearchAsync("keep", _workspaceDir);
        Assert.IsTrue(r1.Matches.Count >= 1, "Kept file still searchable");

        var r2 = await engine.SearchAsync("remove", _workspaceDir);
        Assert.AreEqual(0, r2.Matches.Count, "Deleted file should not be found after rebuild");
    }

    // ── 分词能力：中文 2-gram 分词质量 ──

    [TestMethod]
    public async Task Tokenization_Quality_Chinese_Bigram()
    {
        var analyzer = new JiebaAnalyzer();

        // 创建包含中文内容的文件
        File.WriteAllText(Path.Combine(_workspaceDir, "chinese.txt"),
            "智能体 Agent 平台 全文搜索");

        using var engine = CreateEngine(analyzer);
        await engine.BuildIndexAsync(_workspaceDir);

        // "智能体" 经 CJKBigramFilter → "智能", "能体"
        var r1 = await engine.SearchAsync("智能体", _workspaceDir);
        Assert.IsTrue(r1.Matches.Count >= 1, "Bigram should match partial: '智能体' → '智能' or '能体'");

        var r2 = await engine.SearchAsync("全文搜索", _workspaceDir);
        Assert.IsTrue(r2.Matches.Count >= 1, "Bigram should match: '全文搜索' → '全文' or '文搜' or '搜索'");

        var r3 = await engine.SearchAsync("Agent", _workspaceDir);
        Assert.IsTrue(r3.Matches.Count >= 1, "English word 'Agent' should match directly");
    }

    // ── TXT 文档检索 ──

    [TestMethod]
    public async Task Txt_Document_Retrieval()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "readme.txt"),
            "# Project Overview\n\nThis is a Pudding full-text search demo.\n\n");

        await engine.BuildIndexAsync(_workspaceDir);

        var r1 = await engine.SearchAsync("Pudding", _workspaceDir);
        Assert.IsTrue(r1.Matches.Any(m => m.FilePath.Contains("readme.txt")), "Should find 'Pudding' in readme.txt");

        var r2 = await engine.SearchAsync("Overview", _workspaceDir);
        Assert.IsTrue(r2.Matches.Any(m => m.FilePath.Contains("readme.txt")), "Should find 'Overview' (case-insensitive)");

        var r3 = await engine.SearchAsync("nonexistent", _workspaceDir);
        Assert.AreEqual(0, r3.Matches.Count, "Should not find non-existent text");
    }

    // ── XML 文档检索 ──

    [TestMethod]
    public async Task Xml_Document_Retrieval()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "config.xml"),
            @"<?xml version=""1.0""?>
<configuration>
  <appSettings>
    <add key=""DatabaseUrl"" value=""server=localhost;database=pudding"" />
  </appSettings>
</configuration>");

        await engine.BuildIndexAsync(_workspaceDir);

        var r1 = await engine.SearchAsync("DatabaseUrl", _workspaceDir);
        Assert.IsTrue(r1.Matches.Count >= 1, "Should find XML attribute value");

        var r2 = await engine.SearchAsync("localhost", _workspaceDir);
        Assert.IsTrue(r2.Matches.Count >= 1, "Should find 'localhost' in XML");
    }

    // ── 空文件和大文件边界 ──

    [TestMethod]
    public async Task Empty_File_Skipped_During_Index()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "empty.txt"), "");
        File.WriteAllText(Path.Combine(_workspaceDir, "valid.txt"), "some content");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "Empty file should be skipped");
    }

    [TestMethod]
    public async Task Large_File_Respects_Size_Limit()
    {
        var options = new FullTextIndexOptions
        {
            IndexRootDirectory = _tempDataDir,
            MaxFileSizeBytes = 100, // very small limit
        };
        using var engine = new LuceneSearchEngine(options);

        // 小文件（可索引）
        File.WriteAllText(Path.Combine(_workspaceDir, "small.txt"), "hello");
        // 大文件（超出限制）
        File.WriteAllText(Path.Combine(_workspaceDir, "large.txt"), new string('x', 200));

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "Only small.txt should be indexed");
    }

    // ── 排除规则：node_modules 等黑名单目录 ──

    [TestMethod]
    public async Task Excludes_NodeModules_Directory()
    {
        using var engine = CreateEngine();

        // 正常文件
        File.WriteAllText(Path.Combine(_workspaceDir, "readme.txt"), "hello");
        // node_modules 内文件
        var nmDir = Path.Combine(_workspaceDir, "node_modules", "pkg");
        Directory.CreateDirectory(nmDir);
        File.WriteAllText(Path.Combine(nmDir, "index.ts"), "should be excluded");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "node_modules should be excluded");
    }

    [TestMethod]
    public async Task Excludes_DotGit_Directory()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "src.cs"), "public class Test {}");
        var gitDir = Path.Combine(_workspaceDir, ".git", "objects");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "config.txt"), "should be excluded");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, ".git should be excluded");
    }

    [TestMethod]
    public async Task Excludes_DotVscode_Directory()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "main.py"), "print('hello')");
        var vscDir = Path.Combine(_workspaceDir, ".vscode");
        Directory.CreateDirectory(vscDir);
        File.WriteAllText(Path.Combine(vscDir, "settings.json"), "{ }");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, ".vscode should be excluded");
    }

    [TestMethod]
    public async Task Excludes_Bin_Directory()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "Program.cs"), "class Program {}");
        var binDir = Path.Combine(_workspaceDir, "bin", "Debug");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "output.json"), "{ }");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "bin should be excluded");
    }

    [TestMethod]
    public async Task Excludes_Obj_Directory()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "App.cs"), "class App {}");
        var objDir = Path.Combine(_workspaceDir, "obj", "Release");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "build.props"), "<Project />");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "obj should be excluded");
    }

    [TestMethod]
    public async Task Excludes_Dist_Directory()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "index.ts"), "export {}");
        var distDir = Path.Combine(_workspaceDir, "dist");
        Directory.CreateDirectory(distDir);
        File.WriteAllText(Path.Combine(distDir, "bundle.js"), "minified content");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "dist should be excluded");
    }

    [TestMethod]
    public async Task Excludes_Lock_Files()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "src.cs"), "code");
        File.WriteAllText(Path.Combine(_workspaceDir, "package-lock.json"), "{}");
        File.WriteAllText(Path.Combine(_workspaceDir, "yarn.lock"), "lock");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "lock files should be excluded");
    }

    [TestMethod]
    public async Task Excludes_Nested_Blacklisted_Directories()
    {
        using var engine = CreateEngine();

        // deep nested: a/b/node_modules/pkg/index.ts
        var deepDir = Path.Combine(_workspaceDir, "a", "b", "node_modules", "pkg");
        Directory.CreateDirectory(deepDir);
        File.WriteAllText(Path.Combine(deepDir, "index.ts"), "excluded");

        var srcDir = Path.Combine(_workspaceDir, "src", "lib");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "code.cs"), "included");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "Only src/lib/code.cs should be indexed");
    }

    [TestMethod]
    public async Task Excludes_Case_Insensitive()
    {
        using var engine = CreateEngine();

        File.WriteAllText(Path.Combine(_workspaceDir, "readme.md"), "ok");
        // Node_Modules (mixed case)
        var nmDir = Path.Combine(_workspaceDir, "Node_Modules", "pkg");
        Directory.CreateDirectory(nmDir);
        File.WriteAllText(Path.Combine(nmDir, "index.js"), "excluded");

        var result = await engine.BuildIndexAsync(_workspaceDir);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.IndexedFileCount, "Node_Modules should be excluded case-insensitively");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private LuceneSearchEngine CreateEngine(Analyzer? analyzer = null)
    {
        var options = new FullTextIndexOptions
        {
            IndexRootDirectory = _tempDataDir,
        };
        return analyzer is null
            ? new LuceneSearchEngine(options)
            : new LuceneSearchEngine(options, analyzer, [new PlainTextExtractor()]);
    }
}

// ══════════════════════════════════════════════════════════════════
// 对比测试：Lucene 全文索引 vs. 暴力 Directory.GetFiles + File.ReadAllLines grep
// ══════════════════════════════════════════════════════════════════

[TestClass]
public sealed class GrepVsLuceneBenchmark
{
    private string _tempDataDir = null!;
    private string _workspaceDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"pudding-bench-{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(_tempDataDir, "workspace");
        Directory.CreateDirectory(_tempDataDir);
        Directory.CreateDirectory(_workspaceDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDataDir))
            Directory.Delete(_tempDataDir, recursive: true);
    }

    /// <summary>
    /// 暴力 grep：模拟旧 SearchGrepTool 托管 fallback 的行为。
    /// Directory.GetFiles → File.ReadAllLines → 逐行 Contains。
    /// </summary>
    private static (int matches, long elapsedMs) BruteForceGrep(string directory, string query, string filePattern)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var matches = 0;

        var allFiles = new List<string>();
        foreach (var fp in filePattern.Split(';', StringSplitOptions.TrimEntries))
        {
            try { allFiles.AddRange(Directory.GetFiles(directory, fp, SearchOption.AllDirectories)); }
            catch { /* skip */ }
        }

        foreach (var file in allFiles)
        {
            try
            {
                if (new FileInfo(file).Length > 1024 * 1024) continue; // skip >1MB
                var lines = File.ReadAllLines(file);
                foreach (var line in lines)
                {
                    if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        matches++;
                }
            }
            catch { /* skip */ }
        }

        sw.Stop();
        return (matches, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Lucene 索引搜索：一次 BuildIndex + 一次 Search。
    /// </summary>
    private static async Task<(int matches, long indexMs, long searchMs)> LuceneSearch(
        string directoryPath, string query, string indexRoot, string? filePattern = null)
    {
        var options = new FullTextIndexOptions { IndexRootDirectory = indexRoot };
        using var engine = new LuceneSearchEngine(options);

        var build = await engine.BuildIndexAsync(directoryPath, filePatterns: filePattern);
        var search = await engine.SearchAsync(query, directoryPath, maxResults: 500);

        return (search.TotalMatches, build.ElapsedMs, search.ElapsedMs);
    }

    // ── 对比 1：200 个文件的目录 ──

    [TestMethod]
    public async Task Comparison_200_Files()
    {
        // 生成 200 个 .cs 文件
        var keyword = "PuddinBenchmark";
        for (var i = 0; i < 200; i++)
        {
            var content = i % 10 == 0
                ? $"class Test{i} {{ void Run() {{ var needle = \"{keyword}\"; }} }}"
                : $"class Test{i} {{ void Run() {{ var x = {i}; }} }}";
            File.WriteAllText(Path.Combine(_workspaceDir, $"Test{i}.cs"), content);
        }

        // 暴力 grep
        var (bfMatches, bfMs) = BruteForceGrep(_workspaceDir, keyword, "*.cs");

        // Lucene
        var (luMatches, luIndexMs, luSearchMs) = await LuceneSearch(
            _workspaceDir, keyword, _tempDataDir, filePattern: ".cs");

        Console.WriteLine($"=== 200 files ===");
        Console.WriteLine($"BruteForce: {bfMatches} matches in {bfMs}ms");
        Console.WriteLine($"Lucene:     {luMatches} matches, index={luIndexMs}ms, search={luSearchMs}ms, total={luIndexMs + luSearchMs}ms");
        Console.WriteLine($"Speedup:    {(double)bfMs / luSearchMs:F1}x (search only), {(double)bfMs / (luIndexMs + luSearchMs):F1}x (index+search)");

        Assert.AreEqual(20, bfMatches, "20 out of 200 files contain keyword");
        Assert.IsTrue(luMatches >= 20, "Lucene should find at least 20 matches");
        Assert.IsTrue(luSearchMs < 200, $"Lucene search should be well under 200ms, got {luSearchMs}ms");
    }

    // ── 对比 2：1000 个文件（放大规模） ──

    [TestMethod]
    [Timeout(60_000)]
    public async Task Comparison_1000_Files()
    {
        var keyword = "UniqueNeedleXYZ";
        for (var i = 0; i < 1000; i++)
        {
            var content = i % 20 == 0
                ? $"class Test{i} {{ string needle = \"{keyword}\"; }}"
                : $"class Test{i} {{ int x = {i}; string y = \"some filler text here\"; }}";
            File.WriteAllText(Path.Combine(_workspaceDir, $"Test{i}.cs"), content);
        }

        // 暴力 grep
        var (bfMatches, bfMs) = BruteForceGrep(_workspaceDir, keyword, "*.cs");

        // Lucene
        var (luMatches, luIndexMs, luSearchMs) = await LuceneSearch(
            _workspaceDir, keyword, _tempDataDir, filePattern: ".cs");

        Console.WriteLine($"=== 1000 files ===");
        Console.WriteLine($"BruteForce: {bfMatches} matches in {bfMs}ms");
        Console.WriteLine($"Lucene:     {luMatches} matches, index={luIndexMs}ms, search={luSearchMs}ms, total={luIndexMs + luSearchMs}ms");
        Console.WriteLine($"Speedup:    {(double)bfMs / luSearchMs:F1}x (search only), {(double)bfMs / (luIndexMs + luSearchMs):F1}x (index+search)");

        Assert.AreEqual(50, bfMatches, "50 out of 1000 files contain keyword");
        Assert.IsTrue(luMatches >= 50, "Lucene should find at least 50 matches");
        Assert.IsTrue(luSearchMs < bfMs, $"Lucene search ({luSearchMs}ms) should beat brute force ({bfMs}ms) at 1000 files");
    }

    // ── 对比 3：真实目录 PuddingRuntime ──

    [TestMethod]
    [Timeout(120_000)]
    public async Task Comparison_PuddingRuntime_Real_Directory()
    {
        var runtimeDir = @"D:\WangXianQiang\github\hyfree\PuddingAgent\Source\PuddingRuntime";
        if (!Directory.Exists(runtimeDir))
        {
            Assert.Inconclusive($"Directory not found: {runtimeDir}");
            return;
        }

        // 暴力 grep（只搜 .cs）
        var (bfMatches, bfMs) = BruteForceGrep(runtimeDir, "SearchGrepTool", "*.cs");

        // Lucene
        var (luMatches, luIndexMs, luSearchMs) = await LuceneSearch(
            runtimeDir, "SearchGrepTool", _tempDataDir, filePattern: ".cs");

        Console.WriteLine($"=== PuddingRuntime (real) ===");
        Console.WriteLine($"BruteForce: {bfMatches} matches in {bfMs}ms");
        Console.WriteLine($"Lucene:     {luMatches} matches, index={luIndexMs}ms, search={luSearchMs}ms, total={luIndexMs + luSearchMs}ms");
        Console.WriteLine($"Speedup:    {(double)bfMs / luSearchMs:F1}x (search only), {(double)bfMs / (luIndexMs + luSearchMs):F1}x (index+search)");

        Assert.IsTrue(bfMatches > 0, "Brute force should find matches");
        Assert.IsTrue(luMatches > 0, "Lucene should find matches");
    }

    // ── 对比 4：多次搜索摊销索引成本 ──

    [TestMethod]
    [Timeout(60_000)]
    public async Task Comparison_Repeated_Searches_Amortize_Index_Cost()
    {
        var keywords = new[] { "alpha", "beta", "gamma", "delta", "epsilon" };
        for (var i = 0; i < 500; i++)
        {
            var kws = string.Join(" ", keywords.Select((kw, j) => i % (j + 5) == 0 ? kw : ""));
            File.WriteAllText(Path.Combine(_workspaceDir, $"F{i}.cs"),
                $"class F{i} {{ string text = \"{kws}\"; }}");
        }

        // 暴力 × 5 次搜索
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < keywords.Length; i++)
        {
            BruteForceGrep(_workspaceDir, keywords[i], "*.cs");
        }
        sw.Stop();
        var bfTotalMs = sw.ElapsedMilliseconds;

        // Lucene × 5 次搜索（含一次索引）
        sw.Restart();
        var options = new FullTextIndexOptions { IndexRootDirectory = _tempDataDir };
        using var engine = new LuceneSearchEngine(options);
        await engine.BuildIndexAsync(_workspaceDir, filePatterns: ".cs");
        for (var i = 0; i < keywords.Length; i++)
        {
            await engine.SearchAsync(keywords[i], _workspaceDir);
        }
        sw.Stop();
        var luTotalMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"=== 5 searches on 500 files ===");
        Console.WriteLine($"BruteForce 5x: {bfTotalMs}ms");
        Console.WriteLine($"Lucene 1 index + 5 search: {luTotalMs}ms");
        Console.WriteLine($"Speedup: {(double)bfTotalMs / luTotalMs:F1}x");
    }
}
