using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingFullTextIndex.Contracts;
using PuddingRuntime.Services.Search;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class SearchGrepToolTests
{
    [TestMethod]
    public async Task ExecuteAsync_Uses_Lucene_Index_When_Available()
    {
        var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
            true,
            [
                new FullTextSearchMatch("C:\\temp\\Program.cs", 5, "        var needle = \"NeedleTarget\";"),
            ],
            null, 1, 5));

        var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

        var result = await ExecuteAsync(tool, "NeedleTarget", new Dictionary<string, string> { ["pattern"] = "*.cs", ["max_results"] = "5" });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "Program.cs:5");
    }

    [TestMethod]
    public async Task ExecuteAsync_Falls_Back_To_Managed_Grep_When_Not_Indexed()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "alpha\nNeedle\nomega\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.txt", ["max_results"] = "5" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "sample.txt:2");
            StringAssert.Contains(result.Output, "sample.txt:2: Needle");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_Requires_Query()
    {
        var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance,
            new StubFullTextSearchEngine(false, null!));

        var result = await ExecuteAsync(tool, "", new Dictionary<string, string>());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "query is required");
    }

    [TestMethod]
    public async Task ExecuteAsync_CaseSensitive_Fallback_To_ManagedGrep()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "test.txt"), "hello NEEDLE world");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true,
                new FullTextSearchResult(true, [], null, 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            // case_sensitive=true "Needle" (小写n) 不匹配 "NEEDLE"
            var r1 = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.txt", ["case_sensitive"] = "true" });
            Assert.IsTrue(r1.Success);
            StringAssert.Contains(r1.Output, "(no matches)");

            // "NEEDLE" 大写匹配
            var r2 = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt", ["case_sensitive"] = "true" });
            Assert.IsTrue(r2.Success);
            StringAssert.Contains(r2.Output, "test.txt:1");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

        [TestMethod]
    public async Task ExecuteAsync_ExcludeDirs_Skips_Excluded_Subdirectory()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(tempDir, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "main.cs"), "Needle");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "node_modules", "lib.cs"), "Needle");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            // 默认排除 node_modules
            var result = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.cs", ["max_results"] = "5" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "main.cs");
            Assert.IsFalse(result.Output.Contains("node_modules"), "node_modules should be excluded");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_CustomExcludeDirs_Overrides_Default()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(tempDir, "tests"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "main.cs"), "Needle");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "tests", "test.cs"), "Needle");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            // 自定义排除 tests
            var result = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.cs", ["max_results"] = "5", ["exclude_dirs"] = "tests" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "main.cs");
            Assert.IsFalse(result.Output.Contains("tests"), "tests should be excluded");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_EmptyExcludeDirs_Disables_Default()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "bin"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "bin", "output.cs"), "Needle");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            // exclude_dirs="" 禁用默认排除
            var result = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.cs", ["max_results"] = "5", ["exclude_dirs"] = "" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "output.cs");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_SingleLine_Truncated_When_Line_TooLong()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var longLine = "NEEDLE-" + new string('x', 20000);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "bundle.js"), longLine + Environment.NewLine);

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.js", ["max_results"] = "5" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "bundle.js:1");
            StringAssert.Contains(result.Output, "[truncated, original=");
            Assert.IsFalse(result.Output.Contains(new string('x', 20000)), "the full long line must not be returned");
            Assert.IsTrue(Encoding.UTF8.GetByteCount(result.Output) < 9000, "output must stay near the 8KB single-line cap");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_TotalBytesCap_Stops_Appending()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        for (int i = 1; i <= 10; i++)
            await File.WriteAllTextAsync(Path.Combine(tempDir, $"f{i}.txt"), "NEEDLE-" + new string('a', 100) + Environment.NewLine);

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["pattern"] = "*.txt",
                ["max_results"] = "50",
                ["max_total_bytes"] = "300",
            });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "结果已截断，共命中");
            StringAssert.Contains(result.Output, "请缩小范围");
            StringAssert.Contains(result.Output, "f1.txt:1");
            Assert.IsFalse(result.Output.Contains("f9.txt:1"), "matches beyond the total cap must be dropped");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_DefaultExcludeDirs_Skips_BuildArtifacts()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        foreach (var d in new[] { "$outputWwwroot", "dist", "node_modules", "bin", "obj", ".git", "TestResults", "artifacts", "publish", ".venv", ".tmp" })
            Directory.CreateDirectory(Path.Combine(tempDir, d));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "main.cs"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "$outputWwwroot", "bundle.js"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "dist", "bundle.js"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "node_modules", "lib.js"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "bin", "out.cs"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "obj", "gen.cs"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, ".git", "config"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "TestResults", "res.txt"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "artifacts", "out.cs"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "publish", "out.cs"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, ".venv", "lib.cs"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, ".tmp", "scratch.cs"), "NEEDLE");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["max_results"] = "50" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "main.cs");
            Assert.IsFalse(result.Output.Contains("bundle.js"), "$outputWwwroot/dist must be excluded");
            Assert.IsFalse(result.Output.Contains("lib.js"), "node_modules must be excluded");
            Assert.IsFalse(result.Output.Contains("gen.cs"), "obj must be excluded");
            Assert.IsFalse(result.Output.Contains("res.txt"), "TestResults must be excluded");
            Assert.IsFalse(result.Output.Contains("scratch.cs"), ".tmp must be excluded");
            Assert.IsFalse(result.Output.Contains("out.cs"), "bin/artifacts/publish must be excluded");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ExcludeDirsAppend_Adds_To_Defaults()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(tempDir, "custom_out"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "main.cs"), "NEEDLE");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "custom_out", "gen.cs"), "NEEDLE");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["max_results"] = "50",
                ["exclude_dirs_append"] = "custom_out",
            });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "main.cs");
            Assert.IsFalse(result.Output.Contains("gen.cs"), "appended exclude dir must be skipped");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_BinaryFile_Is_Skipped()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var binBytes = new List<byte>();
        binBytes.AddRange(Encoding.ASCII.GetBytes("NEEDLE-binary-payload"));
        binBytes.Add(0); // NUL 字节 → 二进制文件
        binBytes.AddRange(Encoding.ASCII.GetBytes("-rest"));
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "data.bin"), binBytes.ToArray());
        await File.WriteAllTextAsync(Path.Combine(tempDir, "ok.txt"), "NEEDLE text");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["max_results"] = "50" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "ok.txt");
            Assert.IsFalse(result.Output.Contains("data.bin"), "binary files must be skipped entirely");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_Regression_SmallFiles_Unchanged()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a.txt"), "first line\nNEEDLE here\nthird line\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "b.txt"), "no match\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt", ["max_results"] = "10" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "a.txt:2: NEEDLE here");
            Assert.IsFalse(result.Output.Contains("b.txt"));
            Assert.IsFalse(result.Output.Contains("[truncated"), "small lines must not be truncated");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_LargeExcludedDir_DoesNot_Starve_SourceFiles()
    {
        // 回归：bin/obj 等排除目录中的大量文件不得占用 MaxEnumeratedFiles(2000) 枚举名额，
        // 否则真实源码目录会被跳过，产生假阴性。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var binDir = Path.Combine(tempDir, "bin");
        Directory.CreateDirectory(binDir);
        for (int i = 0; i < 2100; i++)
            await File.WriteAllTextAsync(Path.Combine(binDir, $"asset{i:D4}.js"), "// build output\n");
        var srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);
        await File.WriteAllTextAsync(Path.Combine(srcDir, "main.cs"), "NEEDLE\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            // 默认 pattern（*.*）：bin 目录 2100 个文件若占用枚举名额，src 将永远不被扫描
            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["max_results"] = "10" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "main.cs");
            Assert.IsFalse(result.Output.Contains("asset"), "bin 必须在枚举阶段被剪枝");
            Assert.IsFalse(result.Output.Contains("文件枚举已达上限"), "剪枝后不应触发枚举截断");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_EnumerationTruncation_Is_Declared()
    {
        // 回归：枚举达到 MaxEnumeratedFiles(2000) 上限时必须在输出中声明，避免静默截断。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        for (int i = 0; i < 2050; i++)
            await File.WriteAllTextAsync(Path.Combine(tempDir, $"f{i:D4}.txt"), "NEEDLE\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["max_results"] = "50" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "文件枚举已达上限 2000 个");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_MoreThan100Results_Are_Persisted_To_Temp_File()
    {
        // 回归：maxResults 不再是托管分支的硬上限。即使 max_results=10，
        // 仍持续收集全部命中；总数 >100 时内联前 100 条并把完整结果写入临时文件。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        const int totalMatches = 120;
        for (int i = 1; i <= totalMatches; i++)
            await File.WriteAllTextAsync(Path.Combine(tempDir, $"f{i:D3}.txt"), "NEEDLE line\n");

        string? tmpPath = null;
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["pattern"] = "*.txt",
                ["max_results"] = "10",
            });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "超过预算 100");
            StringAssert.Contains(result.Output, "file_read");
            StringAssert.Contains(result.Output, "OffsetLines");

            // 从报告行提取临时文件路径
            const string marker = "路径为 ";
            var idx = result.Output.IndexOf(marker, StringComparison.Ordinal);
            Assert.IsTrue(idx >= 0, "output must contain temp file path marker");
            var pathStart = idx + marker.Length;
            var commaIdx = result.Output.IndexOf('，', pathStart);
            Assert.IsTrue(commaIdx > pathStart, "temp file path must be delimited by full-width comma");
            tmpPath = result.Output.Substring(pathStart, commaIdx - pathStart);

            // 临时文件存在，内容行数 == 结果总数
            Assert.IsTrue(File.Exists(tmpPath), "temp file must exist");
            var lines = await File.ReadAllLinesAsync(tmpPath);
            Assert.AreEqual(totalMatches, lines.Length, "temp file must contain all matches");
            Assert.AreEqual("f001.txt:1: NEEDLE line", lines[0], "temp file entries must use relPath:lineNumber: text format");

            // 内联恰返回 100 条
            var inlineCount = result.Output.Split('\n').Count(l => l.Contains(":1: NEEDLE"));
            Assert.AreEqual(100, inlineCount, "exactly 100 matches must be inline");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            if (tmpPath != null && File.Exists(tmpPath)) File.Delete(tmpPath);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_100OrFewer_Results_Are_Inline_Without_Temp_File()
    {
        // 边界：恰 100 条命中时行为不变，全部内联，不产生临时文件与分页报告。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        const int totalMatches = 100;
        for (int i = 1; i <= totalMatches; i++)
            await File.WriteAllTextAsync(Path.Combine(tempDir, $"f{i:D3}.txt"), "NEEDLE line\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["pattern"] = "*.txt",
                ["max_results"] = "200",
            });

            Assert.IsTrue(result.Success, result.Error);
            var inlineCount = result.Output.Split('\n').Count(l => l.Contains(":1: NEEDLE"));
            Assert.AreEqual(totalMatches, inlineCount, "all matches must be inline");
            Assert.IsFalse(result.Output.Contains("超过预算"), "no pagination report expected when <=100 results");
            Assert.IsFalse(result.Output.Contains("临时文件"), "no temp file report expected when <=100 results");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

        [TestMethod]
    public async Task ExecuteAsync_CaseSensitive_Accepts_Truthy_Alias_One()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "test.txt"), "hello NEEDLE world");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(true, [], null, 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            // case_sensitive="1" 归一化为 true：小写 needle 不匹配大写 NEEDLE
            var r1 = await ExecuteAsync(tool, "needle", new Dictionary<string, string> { ["pattern"] = "*.txt", ["case_sensitive"] = "1" });
            Assert.IsTrue(r1.Success, r1.Error);
            Assert.AreEqual(ToolResultStatuses.NoMatch, r1.Status);
            StringAssert.Contains(r1.Output, "(no matches)");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_NoMatch_Is_Success_With_NoMatch_Status()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "alpha\nomega\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false, new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var r1 = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            Assert.IsTrue(r1.Success, r1.Error);
            Assert.AreEqual(ToolResultStatuses.NoMatch, r1.Status);
            StringAssert.Contains(r1.Output, "(no matches)");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ShortCircuits_Exact_NoMatch_Retry()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "alpha\nomega\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false, new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var ledger = new SearchAttemptLedger();
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine, ledger: ledger);

            var r1 = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            Assert.IsTrue(r1.Success, r1.Error);
            Assert.AreEqual(ToolResultStatuses.NoMatch, r1.Status);

            var r2 = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            Assert.IsTrue(r2.Success, r2.Error);
            Assert.AreEqual(ToolResultStatuses.ExactRetrySuppressed, r2.Status);
            StringAssert.Contains(r2.Output, "exact retry suppressed");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_DoesNotShortCircuit_DifferentQuery()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "alpha\nomega\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false, new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var ledger = new SearchAttemptLedger();
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine, ledger: ledger);

            var r1 = await ExecuteAsync(tool, "Needle", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            Assert.AreEqual(ToolResultStatuses.NoMatch, r1.Status);

            // 不同 query 不应被短路
            var r2 = await ExecuteAsync(tool, "AnotherNeedle", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            Assert.IsTrue(r2.Success, r2.Error);
            Assert.AreEqual(ToolResultStatuses.NoMatch, r2.Status);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void SkillId_Is_SearchGrep()
    {
        var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance,
            new StubFullTextSearchEngine(false, null!));
        Assert.AreEqual("search_grep", tool.Descriptor.ToolId);
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        SearchGrepTool tool,
        string query,
        IReadOnlyDictionary<string, string> parameters)
    {
        var args = parameters.ToDictionary(
            p => p.Key,
            p => (object?)p.Value,
            StringComparer.OrdinalIgnoreCase);
        args["query"] = query;

        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(args),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
            },
        });
    }

    private sealed class StubFullTextSearchEngine : IFullTextSearchEngine
    {
        private readonly bool _hasIndex;
        private readonly FullTextSearchResult _searchResult;
        public StubFullTextSearchEngine(bool hasIndex, FullTextSearchResult r) { _hasIndex = hasIndex; _searchResult = r; }
        public bool HasIndex(string d) => _hasIndex;
        public Task<FullTextSearchResult> SearchAsync(
            string q,
            string d,
            int m = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default) => Task.FromResult(_searchResult);
        public Task<FullTextIndexResult> BuildIndexAsync(string d, string? fp, CancellationToken ct) => Task.FromResult(new FullTextIndexResult(true, 0, 0, 0, null));
        public bool RemoveIndex(string d) => true;
    }
}
