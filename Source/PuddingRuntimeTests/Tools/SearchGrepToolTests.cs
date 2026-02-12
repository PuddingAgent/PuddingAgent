using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingFullTextIndex.Contracts;
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
