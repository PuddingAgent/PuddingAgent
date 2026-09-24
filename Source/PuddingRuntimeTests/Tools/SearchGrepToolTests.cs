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
        // ADR-089 U0 R1：候选只决定优先读取哪个文件；输出必须是当前内容的真实行号与文本，
        // 旧索引的行号/文本不得返回。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var programPath = Path.Combine(tempDir, "Program.cs");
        await File.WriteAllLinesAsync(programPath,
        [
            "using System;",
            "",
            "class Program",
            "{",
            "        var needle = \"NeedleTarget\";",
            "}",
        ]);

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [
                    new FullTextSearchMatch(programPath, 3, "class Program // stale index snapshot"),
                ],
                null, 1, 5));

            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NeedleTarget", new Dictionary<string, string> { ["pattern"] = "*.cs", ["max_results"] = "5" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "Program.cs:5");
            StringAssert.Contains(result.Output, "var needle = \"NeedleTarget\";");
            Assert.IsFalse(result.Output.Contains("stale index snapshot"), "stale index text must never be returned");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
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
            StringAssert.Contains(result.Output, "code_symbol_search");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_EnumerationTruncation_Guides_To_Indexed_Tools()
    {
        // 枚举截断 note 必须同时声明上限并引导改用索引工具（code_symbol_search 等毫秒级返回）。
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
            StringAssert.Contains(result.Output, "code_symbol_search");
            Assert.AreEqual(ToolResultStatuses.Truncated, result.Status);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_LargeFiles_Are_Declared_As_Skipped()
    {
        // 回归：超过 1MB 的大文件被静默跳过时必须在输出中声明，否则"小文件命中但大文件内容丢失"对调用方不可见。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var bigLine = new string('x', 1024) + " NEEDLE\n";
        var bigContent = string.Concat(Enumerable.Repeat(bigLine, 1100)); // ≈1.13MB，含 NEEDLE 但必须被跳过
        await File.WriteAllTextAsync(Path.Combine(tempDir, "big.txt"), bigContent);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "small.txt"), "tiny NEEDLE sample.\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt", ["max_results"] = "10" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "small.txt:1: tiny NEEDLE sample.");
            StringAssert.Contains(result.Output, "已跳过 1 个超过 1MB 的大文件");
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
        // 回归（ADR-089 U0 R4 后口径）：max_results 是合并结果集的上限；
        // max_results=150 > 120 时不截断，全部命中被收集；总数 >100 时内联前 100 条并把完整结果写入临时文件。
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
                ["max_results"] = "150",
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

    // ── ADR-089 U0-S2：候选-复核-覆盖改造新增用例 ─────────────────────────────

    [TestMethod]
    public async Task U0S2_LuceneCandidate_Failing_Exact_Review_Is_Dropped()
    {
        // 分词假阳性防御：Lucene 召回只是候选，query "foo_bar" 不得命中被切词的 "public void Foo()"。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "code.txt"), "public void Foo()\nnothing relevant\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "code.txt"), 1, "public void Foo()")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "foo_bar", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.IsFalse(result.Output.Contains("Foo()"), "token-recall candidate must be dropped by exact review");
            Assert.AreEqual(ToolResultStatuses.NoMatch, result.Status);
            StringAssert.Contains(result.Output, "(no matches)");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0S2_LuceneZeroCandidates_SourceHit_Still_Returned_As_Complete()
    {
        // L128：Lucene 无候选不能证明无命中；托管扫描必须继续并给出命中，覆盖为 Complete。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "Needle in source\nfiller\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true,
                new FullTextSearchResult(true, [], null, 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "Needle in source", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
            StringAssert.Contains(result.Output, "sample.txt:1: Needle in source");
            Assert.IsFalse(result.Output.Contains("(coverage: partial"), "complete scan must not declare partial");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0S2_Candidate_Line_And_Scan_Lines_Merge_Without_Duplicates()
    {
        // 候选行与扫描行合并去重；同文件其它精确命中行不得因整文件跳过而漏掉（假阴性防御）。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "doc.txt"), "NEEDLE first\nfiller\nfiller\nfiller\nNEEDLE fifth\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "doc.txt"), 1, "NEEDLE first")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
            StringAssert.Contains(result.Output, "doc.txt:1: NEEDLE first");
            StringAssert.Contains(result.Output, "doc.txt:5: NEEDLE fifth");
            var hitLines = result.Output.Split('\n').Count(l => l.Contains("NEEDLE"));
            Assert.AreEqual(2, hitLines, "candidate line and scan line must not be duplicated");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0S2_InvalidRegex_Fails_Explicitly_Without_Literal_Fallback()
    {
        // 非法正则明确失败（ContractError），不得静默降级为字面搜索，也不得扫描返回字面命中。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "literal.txt"), "a([ literal content\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "a([", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsFalse(result.Success, "invalid regex must fail, not fall back to literal search");
            Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
            StringAssert.Contains(result.Error, "a([", "contract error must contain the original pattern");
            Assert.IsFalse((result.Output ?? string.Empty).Contains("literal.txt"), "must not scan/return literal fallback hits");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0S2_CaseSensitive_Same_Contract_On_Candidate_And_Scan()
    {
        // 大小写合同在候选复核路径与托管扫描路径一致：同一 query/case 下结论相同。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "needle lower case\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "other.txt"), "NEEDLE UPPER\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            // 旧索引声称 other.txt 行 7 内容 "NEEDLE UPPER"（行号已过期：文件当前只有 1 行）。
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "other.txt"), 7, "NEEDLE UPPER")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            // 敏感：候选 "NEEDLE UPPER" 复核失败被丢弃；扫描命中小写行
            var sensitive = await ExecuteAsync(tool, "needle", new Dictionary<string, string> { ["pattern"] = "*.txt", ["case_sensitive"] = "true" });
            Assert.IsTrue(sensitive.Success, sensitive.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, sensitive.Status);
            StringAssert.Contains(sensitive.Output, "sample.txt:1");
            Assert.IsFalse(sensitive.Output.Contains("NEEDLE UPPER"), "case-sensitive review must drop the mismatching candidate");

            // 不敏感：候选文件存在且当前内容命中，输出当前内容的真实行号（旧索引声称行 7）。
            var insensitive = await ExecuteAsync(tool, "needle", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            Assert.IsTrue(insensitive.Success, insensitive.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, insensitive.Status);
            StringAssert.Contains(insensitive.Output, "other.txt:1: NEEDLE UPPER");
            Assert.IsFalse(insensitive.Output.Contains("other.txt:7"), "stale index line numbers must not be returned");
            StringAssert.Contains(insensitive.Output, "sample.txt:1");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0S2_Chinese_Query_Hits()
    {
        // 中文 query（literal 路径）命中中文内容行。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "这是中文目标行\n其它内容\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "中文目标", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
            StringAssert.Contains(result.Output, "sample.txt:1: 这是中文目标行");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0S2_ByteCap_Truncation_Declares_Partial_And_Never_NoMatch()
    {
        // 覆盖语义：输出字节上限截断 → 非 Complete → 必须带 partial 声明，
        // 空结果不得伪装为 "(no matches)"（no_match 仅表示声明范围已完成）。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "NEEDLE with some longer content\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt", ["max_total_bytes"] = "1" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Truncated, result.Status);
            StringAssert.Contains(result.Output, "(coverage: partial");
            StringAssert.Contains(result.Output, "结果已截断");
            Assert.IsFalse(result.Output.Contains("(no matches)"), "incomplete empty result must not read as no_match");
            // 覆盖声明行必须恰好出现一次（防止 notes 与前置文案重复拼接）。
            Assert.AreEqual(1, result.Output.Split("(coverage: partial").Length - 1,
                "coverage declaration must appear exactly once");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0S2_EmptyResult_NotComplete_Is_Not_Recorded_As_NoMatch()
    {
        // S2-5：空结果 + 非 Complete 不得记为 NoMatch，否则同一查询会被失败账本错误抑制。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "NEEDLE with some longer content\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var ledger = new SearchAttemptLedger();
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine, ledger: ledger);

            var r1 = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt", ["max_total_bytes"] = "1" });
            Assert.AreEqual(ToolResultStatuses.Truncated, r1.Status);

            // 同一查询重试：Truncated 不是确定性终态，不得被 exact-retry 抑制。
            var r2 = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt", ["max_total_bytes"] = "1" });
            Assert.AreNotEqual(ToolResultStatuses.ExactRetrySuppressed, r2.Status, "partial empty result must not suppress retry");
            Assert.AreEqual(ToolResultStatuses.Truncated, r2.Status);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R1_StaleIndexLine_NotReturned_When_CurrentContent_NoMatch()
    {
        // R1：候选只是优先读取提示；当前文件已改写为无 NEEDLE 内容时，
        // 完整扫描后必须 no_match，绝不返回旧索引文本。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "replacement content only\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "sample.txt"), 1, "NEEDLE from stale index")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.NoMatch, result.Status);
            StringAssert.Contains(result.Output, "(no matches)");
            Assert.IsFalse(result.Output.Contains("NEEDLE"), "stale index hit must not be returned");
            Assert.IsFalse(result.Output.Contains("stale"), "stale index text must not leak into output");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R1_ChangedLine_Returns_Current_Content_Not_Index_Text()
    {
        // R1：当前行已改写时，输出必须是当前内容（而非索引旧文本），且真实行号只出现一次。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "doc.txt"), "NEEDLE current content\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "doc.txt"), 1, "NEEDLE old content")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
            StringAssert.Contains(result.Output, "doc.txt:1: NEEDLE current content");
            Assert.IsFalse(result.Output.Contains("old content"), "stale index text must not be returned");
            Assert.AreEqual(1, result.Output.Split('\n').Count(l => l.Contains("doc.txt:")),
                "current line must be emitted exactly once");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R1_DeletedCandidate_Is_Not_Returned()
    {
        // R1：候选文件已从磁盘删除 → 不得作为命中输出、不得绑定旧路径；
        // 声明范围内无现存文件 → 完整覆盖的空结果。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "ghost.txt"), 3, "NEEDLE ghost line")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.NoMatch, result.Status);
            Assert.IsFalse(result.Output.Contains("ghost"), "deleted candidate must not be returned");
            Assert.IsFalse(result.Output.Contains("NEEDLE"), "deleted candidate text must not leak");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R1_NonPureExtensionGlob_Candidate_Is_Filtered()
    {
        // R1：pattern=Keep*.txt（非纯扩展名 glob）时，不符合 glob 的候选 sample.txt
        // 不得进入结果；仅磁盘现存且符合 glob 的 KeepMe.txt 命中。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "NEEDLE in sample\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "KeepMe.txt"), "NEEDLE in keep\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "sample.txt"), 1, "NEEDLE in sample")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "Keep*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
            StringAssert.Contains(result.Output, "KeepMe.txt:1: NEEDLE in keep");
            Assert.IsFalse(result.Output.Contains("sample.txt"), "candidate outside glob must not enter results");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R1_RenamedCandidate_Returns_Only_Existing_Path()
    {
        // R1：候选路径已重命名（旧路径不存在）→ 只返回磁盘现存路径的命中。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "renamed.txt"), "NEEDLE in renamed file\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "old-name.txt"), 1, "NEEDLE old location")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
            StringAssert.Contains(result.Output, "renamed.txt:1: NEEDLE in renamed file");
            Assert.IsFalse(result.Output.Contains("old-name.txt"), "stale candidate path must not be returned");
            Assert.IsFalse(result.Output.Contains("old location"), "stale candidate text must not be returned");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R2_CatastrophicRegex_Bounded_By_SingleCall_Budget_Including_Candidates()
    {
        // R2：两条灾难性回溯候选 + 小预算 → 候选与扫描共享同一 deadline，
        // 整次调用远小于旧实现（每候选各耗一次完整正则超时）的耗时；
        // 返回 timeout 而非 no_match，且不得输出 (no matches)。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var catastrophic = new string('a', 32) + "!";
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a1.txt"), catastrophic + "\nfiller\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a2.txt"), "filler\n" + catastrophic + "\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [
                    new FullTextSearchMatch(Path.Combine(tempDir, "a1.txt"), 1, catastrophic),
                    new FullTextSearchMatch(Path.Combine(tempDir, "a2.txt"), 2, catastrophic),
                ],
                null, 2, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine,
                searchTimeout: TimeSpan.FromMilliseconds(150));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await ExecuteAsync(tool, "^(a+)+$", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            sw.Stop();

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Timeout, result.Status);
            Assert.IsFalse(result.Output.Contains("(no matches)"), "timeout must never read as no_match");
            Assert.IsTrue(sw.ElapsedMilliseconds < 10_000,
                $"entire call must respect the single-call budget (took {sw.ElapsedMilliseconds}ms)");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R2_CancelledCallerToken_Propagates_OperationCanceled()
    {
        // R2.4/R3.3：已取消的调用方令牌必须以 OperationCanceledException 传播，
        // 不得返回 no_match，也不得被记入失败账本。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "NEEDLE in readable file\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => ExecuteAsync(tool, "NEEDLE",
                    new Dictionary<string, string> { ["pattern"] = "*.txt" },
                    new CancellationToken(canceled: true)));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R3_LockedFile_Is_Not_NoMatch_And_Retryable_After_Release()
    {
        // R3：独占锁定含 NEEDLE 的文件 → 非 no_match（truncated + 覆盖声明）；
        // 释放后同一 query 必须能重新执行并返回真实命中（不得被 exact-retry 抑制）。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var lockedPath = Path.Combine(tempDir, "locked.txt");
        await File.WriteAllTextAsync(lockedPath, "NEEDLE behind exclusive lock\n");

        var fs = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var ledger = new SearchAttemptLedger();
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine, ledger: ledger);

            var r1 = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            Assert.IsTrue(r1.Success, r1.Error);
            Assert.AreEqual(ToolResultStatuses.Truncated, r1.Status,
                "locked file must degrade coverage, never fabricate no_match");
            Assert.IsFalse(r1.Output.Contains("(no matches)"), "incomplete empty result must not read as no_match");
            Assert.AreEqual(1, r1.Output.Split("(coverage: partial").Length - 1,
                "coverage declaration must appear exactly once");

            fs.Dispose(); // 释放独占锁

            var r2 = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });
            Assert.IsTrue(r2.Success, r2.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, r2.Status);
            StringAssert.Contains(r2.Output, "locked.txt:1: NEEDLE behind exclusive lock");
        }
        finally
        {
            fs.Dispose();
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R3_SingleReadError_Below_Threshold_Is_Not_Complete_Or_NoMatch()
    {
        // R3：1 个不可读文件（未达 MaxErrors 阈值）也必须使覆盖非 Complete；
        // 声明范围内无其它命中时空结果不得伪装为 no_match。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var lockedPath = Path.Combine(tempDir, "locked.txt");
        await File.WriteAllTextAsync(lockedPath, "NEEDLE hidden by lock\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "clean.txt"), "alpha only\n");

        var fs = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Truncated, result.Status);
            StringAssert.Contains(result.Output, "有 1 个文件读取失败，结果可能不完整");
            StringAssert.Contains(result.Output, "(coverage: partial");
            Assert.IsFalse(result.Output.Contains("(no matches)"), "incomplete empty result must not read as no_match");
        }
        finally
        {
            fs.Dispose();
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R3_SubdirectoryEnumerationFailure_Declares_Incomplete_Coverage()
    {
        // R3：子目录枚举失败（指向不存在目标的悬空联接）→ enumerationErrored →
        // 覆盖非 Complete、状态 truncated、输出声明失败目录数，绝不静默吞掉。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var junction = Path.Combine(tempDir, "broken_link");
        if (!TryCreateDanglingJunction(junction, Path.Combine(tempDir, "missing-target")))
        {
            Assert.Inconclusive("dangling junction cannot be created on this volume; enumeration-error path unverified");
            return;
        }
        await File.WriteAllTextAsync(Path.Combine(tempDir, "real.txt"), "no needle here\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["pattern"] = "*.txt" });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Truncated, result.Status);
            StringAssert.Contains(result.Output, "1 个子目录枚举失败，结果可能不完整");
            StringAssert.Contains(result.Output, "(coverage: partial");
            Assert.AreEqual(1, result.Output.Split("(coverage: partial").Length - 1,
                "coverage declaration must appear exactly once");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            try { Directory.Delete(junction, recursive: false); } catch { /* best effort */ }
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R4_MaxResults_Caps_Merged_Output_And_Declares_Truncation()
    {
        // R4：单文件 3 行 NEEDLE + max_results=1 → 最终输出恰好 1 行且 truncated（改动前会输出 3 行）。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "NEEDLE one\nfiller\nNEEDLE two\nNEEDLE three\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["pattern"] = "*.txt",
                ["max_results"] = "1",
            });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Truncated, result.Status);
            StringAssert.Contains(result.Output, "sample.txt:1: NEEDLE one");
            Assert.AreEqual(1, result.Output.Split('\n').Count(l => l.Contains("sample.txt:")),
                "output must contain exactly max_results hit lines");
            StringAssert.Contains(result.Output, "达到 max_results 上限（1）");
            Assert.AreEqual(1, result.Output.Split("(coverage: partial").Length - 1,
                "coverage declaration must appear exactly once");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R4_ExactMaxResults_NaturalCompletion_Is_Complete()
    {
        // R4：恰好 maxResults 个匹配且扫描自然完成 → Complete（Ok，无 partial/上限声明）。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.txt"), "NEEDLE one\nNEEDLE two\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["pattern"] = "*.txt",
                ["max_results"] = "2",
            });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
            StringAssert.Contains(result.Output, "sample.txt:1: NEEDLE one");
            StringAssert.Contains(result.Output, "sample.txt:2: NEEDLE two");
            Assert.IsFalse(result.Output.Contains("(coverage: partial"), "natural completion must not declare partial");
            Assert.IsFalse(result.Output.Contains("达到 max_results 上限"), "exact match count must not declare truncation");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task U0R4_Candidate_And_Scan_Merge_Respect_Shared_MaxResults()
    {
        // R4：索引候选文件与枚举扫描文件共用同一 results 计数——
        // cand.txt 1 行命中 + scan.txt 2 行命中、max_results=2 → 恰好 2 行 + truncated + 上限声明（2）。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "cand.txt"), "NEEDLE in candidate\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "scan.txt"), "NEEDLE s1\nfiller\nNEEDLE s2\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(Path.Combine(tempDir, "cand.txt"), 1, "NEEDLE in candidate")],
                null, 1, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["pattern"] = "*.txt",
                ["max_results"] = "2",
            });

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Truncated, result.Status);
            StringAssert.Contains(result.Output, "cand.txt:1: NEEDLE in candidate");
            StringAssert.Contains(result.Output, "scan.txt:1: NEEDLE s1");
            Assert.AreEqual(2, result.Output.Split('\n').Count(l =>
                (l.Contains("cand.txt:") || l.Contains("scan.txt:")) && l.Contains("NEEDLE")),
                "merged output must contain exactly max_results hit lines");
            StringAssert.Contains(result.Output, "达到 max_results 上限（2）");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// 用 mklink /J 创建指向不存在目标的目录联接（无需管理员权限），用于构造子目录枚举失败场景。
    /// 返回是否成功创建（非 NTFS 等环境返回 false，调用方以 Inconclusive 表达环境限制）。
    /// </summary>
    private static bool TryCreateDanglingJunction(string junctionPath, string targetPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return false;
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ─── ADR-089 U0-G2：SearchGrepTool glob 统一到 RetrievalGlobMatcher 的交叉一致性回归 ───

    [TestMethod]
    public async Task Search_Grep_WithTxtGlob_Excludes_Atxtx()
    {
        // 反证旧语义 B（Win32 searchPattern 的 *.txt 会前导匹配 a.txtx）：
        // canonical 合同下 *.txt 必须精确到扩展名边界，a.txtx 不得命中。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a.txt"), "NeedleTarget in a.txt\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a.txtx"), "NeedleTarget in a.txtx\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NeedleTarget", new Dictionary<string, string> { ["pattern"] = "*.txt", ["max_results"] = "10" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "a.txt:1");
            Assert.IsFalse(result.Output.Contains("a.txtx"), "glob '*.txt' must not match 'a.txtx' (Win32 searchPattern leading-match semantics)");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Search_Grep_WithStarDotStarGlob_Matches_Extensionless_Makefile()
    {
        // 反证旧语义 A（MatchesSimpleExpression 的 *.* 要求文件名含点）：
        // canonical 规范 3a 整串 *.* ≡ *，无扩展名文件 Makefile 必须命中。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "Makefile"), "NeedleTarget in makefile\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "readme.txt"), "NeedleTarget in readme\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            // 不传 pattern：默认 filePattern="*.*"
            var result = await ExecuteAsync(tool, "NeedleTarget", new Dictionary<string, string> { ["max_results"] = "10" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "Makefile:1");
            StringAssert.Contains(result.Output, "readme.txt:1");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Search_Grep_WithKeepPrefixGlob_Matches_Keep1Txt()
    {
        // 非纯扩展名 glob（Keep*.txt）完整匹配：Keep1.txt 命中，Skip1.txt 不得命中。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "Keep1.txt"), "NeedleTarget keep\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "Skip1.txt"), "NeedleTarget skip\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NeedleTarget", new Dictionary<string, string> { ["pattern"] = "Keep*.txt", ["max_results"] = "10" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "Keep1.txt:1");
            Assert.IsFalse(result.Output.Contains("Skip1.txt"), "glob 'Keep*.txt' must not match 'Skip1.txt'");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Search_Grep_WithSeparatorGlob_Matches_RelativePath_And_StarNotCrossSlash()
    {
        // 含分隔符 glob 按相对路径匹配；* 不跨 /：src/*.cs 命中 src/a.cs，不命中 src/sub/b.cs。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "src", "sub"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "a.cs"), "NeedleTarget in src a\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "sub", "b.cs"), "NeedleTarget in src sub b\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: false,
                new FullTextSearchResult(false, [], "not indexed", 0, 0));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NeedleTarget", new Dictionary<string, string> { ["pattern"] = "src/*.cs", ["max_results"] = "10" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, $"src{Path.DirectorySeparatorChar}a.cs:1");
            Assert.IsFalse(result.Output.Contains("b.cs"), "glob 'src/*.cs' must not cross '/' to match src/sub/b.cs");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Search_Grep_Candidate_Violating_Glob_Dropped_And_No_Duplicate_Emit()
    {
        // 候选-枚举奇偶校验：Lucene 返回违规候选 a.txtx（违反 *.txt）与合规候选 good.txt；
        // 违规候选必须被准入谓词丢弃，合规候选正常命中且不与枚举路径重复输出。
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "good.txt"), "NeedleTarget good\n");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a.txtx"), "NeedleTarget bad\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var searchEngine = new StubFullTextSearchEngine(hasIndex: true, new FullTextSearchResult(
                true,
                [
                    new FullTextSearchMatch(Path.Combine(tempDir, "a.txtx"), 1, "NeedleTarget bad"),
                    new FullTextSearchMatch(Path.Combine(tempDir, "good.txt"), 1, "NeedleTarget good"),
                ],
                null, 2, 5));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, searchEngine);

            var result = await ExecuteAsync(tool, "NeedleTarget", new Dictionary<string, string> { ["pattern"] = "*.txt", ["max_results"] = "10" });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "good.txt:1");
            Assert.IsFalse(result.Output.Contains("a.txtx"), "candidate violating the glob must be dropped before scan");
            Assert.AreEqual(1, result.Output.Split('\n').Count(l => l.Contains("good.txt:") && l.Contains("NeedleTarget")),
                "good.txt must be emitted exactly once (candidate path merged with enumeration path via processedFiles)");
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
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct = default)
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
        }, ct);
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
            CancellationToken ct = default,
            FullTextSearchScope? scope = null) => Task.FromResult(_searchResult);
        public Task<FullTextIndexResult> BuildIndexAsync(string d, string? fp, CancellationToken ct) => Task.FromResult(new FullTextIndexResult(true, 0, 0, 0, null));
        public bool RemoveIndex(string d) => true;
    }

    // ===== ADR-089 U4-5a：backend 路由（新全文索引后端 / 旧路径兑底）=====

    [TestMethod]
    public async Task Backend_Index_Returns_Index_Hits_Without_Managed_Scan()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var indexed = Path.Combine(tempDir, "indexed.txt");
        var notIndexed = Path.Combine(tempDir, "notindexed.txt");
        await File.WriteAllTextAsync(indexed, "alpha\nNEEDLE-from-index\nomega\n");
        await File.WriteAllTextAsync(notIndexed, "NEEDLE-only-on-disk\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var engine = new RecordingFullTextSearchEngine(new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(indexed, 2, "NEEDLE-from-index")],
                null, 1, 4100));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, engine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["backend"] = "index",
                ["directory"] = tempDir,
            });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "indexed.txt:2: NEEDLE-from-index");
            Assert.IsFalse(result.Output.Contains("notindexed.txt"),
                "backend=index 不得回落到托管扫描：未被索引的文件不得出现在结果里");

            // 观测面：单次检索耗时必须在结果里可见（引擎侧 + 工具侧）
            StringAssert.Contains(result.Output, "backend=index");
            StringAssert.Contains(result.Output, "engineMs=4100");
            StringAssert.Contains(result.Output, "totalMs=");
            StringAssert.Contains(result.Output, "no managed scan");

            // 引擎侧事实：scope = 调用方目录；取 max_results + 1 用于判定截断
            Assert.AreEqual(1, engine.CallCount, "index 后端应只调用引擎一次（不得回落到扫描）");
            Assert.AreEqual(Path.GetFullPath(tempDir), engine.LastDirectory);
            Assert.AreEqual(21, engine.LastMaxResults);
        }
        finally
        {
            RestoreAndDelete(previousCwd, tempDir);
        }
    }

    [TestMethod]
    public async Task Backend_Index_Fails_Closed_When_Scope_Is_Not_Indexed()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "a.txt"), "NEEDLE here\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var engine = new RecordingFullTextSearchEngine(new FullTextSearchResult(
                false, [], $"Directory '{tempDir}' is not indexed.", 0, 3));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, engine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["backend"] = "index",
                ["directory"] = tempDir,
            });

            // fail-closed：不得静默回落到扫描（否则索引坏了也看不出来，且调用方无从得知覆盖度）
            Assert.IsFalse(result.Success, "索引后端不可用时必须显式失败，不得静默降级");
            StringAssert.Contains(result.Error, "Index backend unavailable");
            StringAssert.Contains(result.Error, "omit 'backend'");
            Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
        }
        finally
        {
            RestoreAndDelete(previousCwd, tempDir);
        }
    }

    [TestMethod]
    public async Task Backend_Unknown_Value_Is_A_Contract_Error()
    {
        var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance,
            new RecordingFullTextSearchEngine(new FullTextSearchResult(true, [], null, 0, 0)));

        var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string> { ["backend"] = "lucene" });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Unknown backend 'lucene'");
        StringAssert.Contains(result.Error, "'scan'");
        StringAssert.Contains(result.Error, "'index'");
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
        Assert.IsFalse(result.Output.Contains("NEEDLE"));
    }

    [TestMethod]
    public async Task Backend_Default_Equals_Scan_Byte_For_Byte()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "x.txt"), "alpha\nNEEDLE beta\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var toolDefault = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance,
                new RecordingFullTextSearchEngine(new FullTextSearchResult(false, [], "not indexed", 0, 0)));
            var toolScan = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance,
                new RecordingFullTextSearchEngine(new FullTextSearchResult(false, [], "not indexed", 0, 0)));

            var noParam = await ExecuteAsync(toolDefault, "NEEDLE", new Dictionary<string, string>());
            var scanParam = await ExecuteAsync(toolScan, "NEEDLE", new Dictionary<string, string> { ["backend"] = "scan" });

            Assert.IsTrue(noParam.Success, noParam.Error);
            Assert.IsTrue(scanParam.Success, scanParam.Error);
            Assert.AreEqual(noParam.Output, scanParam.Output, "backend 缺省与 backend='scan' 必须逐字节一致（旧路径零行为变化）");
            Assert.AreEqual(noParam.Status, scanParam.Status);
            StringAssert.Contains(noParam.Output, "x.txt:2");
        }
        finally
        {
            RestoreAndDelete(previousCwd, tempDir);
        }
    }

    [TestMethod]
    public async Task Backend_Index_Rejects_CaseSensitive_Because_Analyzer_Ignores_CaseMode()
    {
        var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance,
            new RecordingFullTextSearchEngine(new FullTextSearchResult(true, [], null, 0, 0)));

        var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
        {
            ["backend"] = "index",
            ["case_sensitive"] = "true",
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "case_sensitive");
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
    }

    [TestMethod]
    public async Task Backend_Index_Skips_Stale_Paths_Whose_File_No_Longer_Exists()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"pudding-sgt-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var fresh = Path.Combine(tempDir, "fresh.txt");
        var deleted = Path.Combine(tempDir, "deleted.txt");
        await File.WriteAllTextAsync(fresh, "NEEDLE fresh\n");

        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var engine = new RecordingFullTextSearchEngine(new FullTextSearchResult(
                true,
                [
                    new FullTextSearchMatch(fresh, 1, "NEEDLE fresh"),
                    new FullTextSearchMatch(deleted, 1, "NEEDLE stale"),
                ],
                null, 2, 7));
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, engine);

            var result = await ExecuteAsync(tool, "NEEDLE", new Dictionary<string, string>
            {
                ["backend"] = "index",
                ["directory"] = tempDir,
            });

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "fresh.txt:1: NEEDLE fresh");
            Assert.IsFalse(result.Output.Contains("NEEDLE stale"),
                "索引快照指向已不存在的文件时不得把它当命中输出");
            StringAssert.Contains(result.Output, "staleSkipped=1");
        }
        finally
        {
            RestoreAndDelete(previousCwd, tempDir);
        }
    }

    private static void RestoreAndDelete(string previousCwd, string tempDir)
    {
        Directory.SetCurrentDirectory(previousCwd);
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// U4-5a：记录调用事实的引擎桩——用于断言 backend=index 真的走了引擎（而不是回落到托管扫描），
    /// 以及传给引擎的 scope / max_results 事实。
    /// </summary>
    private sealed class RecordingFullTextSearchEngine : IFullTextSearchEngine
    {
        private readonly FullTextSearchResult _result;

        public RecordingFullTextSearchEngine(FullTextSearchResult result) => _result = result;

        public int CallCount { get; private set; }
        public string? LastQuery { get; private set; }
        public string? LastDirectory { get; private set; }
        public int LastMaxResults { get; private set; }
        public string? LastExtensionFilter { get; private set; }

        public bool HasIndex(string d) => true;

        public Task<FullTextSearchResult> SearchAsync(
            string q,
            string d,
            int m = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default,
            FullTextSearchScope? scope = null)
        {
            CallCount++;
            LastQuery = q;
            LastDirectory = d;
            LastMaxResults = m;
            LastExtensionFilter = fileExtensionFilter;
            return Task.FromResult(_result);
        }

        public Task<FullTextIndexResult> BuildIndexAsync(string d, string? fp, CancellationToken ct) =>
            Task.FromResult(new FullTextIndexResult(true, 0, 0, 0, null));

        public bool RemoveIndex(string d) => true;
    }
}
