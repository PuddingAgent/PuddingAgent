using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Tools;
using PuddingFullTextIndex.Contracts;
using PuddingRuntime.Services.Search;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 2026-10-28（W2 接线回归）：默认扫描路径的「Lucene 候选预取」必须真的用上索引。
///
/// 修之前是 <c>SearchAsync(query, directory ?? "", …, subDirectoryFilter: directory)</c>：
/// scope 给的是待搜目录自己（未必有索引），而 subDirectoryFilter 要的是**相对 scope 的
/// 相对路径**——传目录本身，引擎用 GetRelativePath 再比前缀永远不命中 ⇒ 候选恒为空，
/// 而且**不报错**：静默失效，只有读代码才发现。
///
/// 本用例锁的就是这两个参数：scope 必须是「有索引的祖先」，过滤必须是「相对该祖先的子路径」。
/// 只断言“结果正确”是不够的——修之前结果同样正确（候选为空时托管扫描照样覆盖），
/// 所以必须直接盯引擎收到的参数。
/// </summary>
[TestClass]
public sealed class SearchGrepCandidateScopeTests
{
    [TestMethod]
    public async Task CandidatePrefetch_UsesIndexedAncestorScope_AndRelativeSubPath()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var root = Path.Combine(Path.GetTempPath(), "pudding-cand-" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(root, "Source", "Sub");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "Needle.cs");
        await File.WriteAllTextAsync(filePath, "// header\nvar needle = \"NeedleTarget\";\n");

        try
        {
            Directory.SetCurrentDirectory(root);

            var engine = new RecordingEngine(indexedRoot: root, matchPath: filePath);
            var tool = new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, engine);

            var result = await ExecuteAsync(tool, "NeedleTarget", new Dictionary<string, string>
            {
                ["directory"] = Path.Combine("Source", "Sub"),
                ["pattern"] = "*.cs",
                ["max_results"] = "5",
            }, workingDirectory: root);

            Assert.IsTrue(result.Success, result.Error);

            Assert.IsNotNull(engine.LastDirectoryPath, "候选预取根本没有调用引擎");
            Assert.AreEqual(
                Normalize(root),
                Normalize(engine.LastDirectoryPath!),
                "scope 必须是有索引的祖先目录；传待搜子目录自身会让引擎 HasIndex 失败、候选恒为空");

            Assert.IsNotNull(
                engine.LastSubDirectoryFilter,
                "必须传相对子目录前缀；传 null 会退化成整棵祖先树（结果面变大）");
            StringAssert.EndsWith(
                engine.LastSubDirectoryFilter!.Replace('/', '\\'),
                "Source\\Sub",
                "过滤前缀必须是相对祖先的相对路径");
            Assert.IsFalse(
                Path.IsPathRooted(engine.LastSubDirectoryFilter),
                "绝对路径会被引擎当相对前缀比较，永远不命中");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        SearchGrepTool tool,
        string query,
        IReadOnlyDictionary<string, string> parameters,
        string workingDirectory,
        CancellationToken ct = default)
    {
        var args = parameters.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
        args["query"] = query;

        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-cand-1",
            ArgumentsJson = JsonSerializer.Serialize(args),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
                // 关键：相对目录按 WorkingDirectory 解析。只设 CWD 不够——工具会走
                // HostFileToolPaths.ResolveWorkspaceRoot，实测直接解析到了仓库根。
                WorkingDirectory = workingDirectory,
            },
        }, ct);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed class RecordingEngine : IFullTextSearchEngine
    {
        private readonly string _indexedRoot;
        private readonly string _matchPath;

        public RecordingEngine(string indexedRoot, string matchPath)
        {
            _indexedRoot = Normalize(indexedRoot);
            _matchPath = matchPath;
        }

        public string? LastDirectoryPath { get; private set; }

        public string? LastSubDirectoryFilter { get; private set; }

        public bool HasIndex(string directoryPath) =>
            string.Equals(Normalize(directoryPath), _indexedRoot, StringComparison.OrdinalIgnoreCase);

        public Task<FullTextSearchResult> SearchAsync(
            string query,
            string directoryPath,
            int maxResults = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default,
            FullTextSearchScope? scope = null)
        {
            LastDirectoryPath = directoryPath;
            LastSubDirectoryFilter = subDirectoryFilter;
            return Task.FromResult(new FullTextSearchResult(
                true,
                [new FullTextSearchMatch(_matchPath, 2, "var needle = \"NeedleTarget\";")],
                null,
                1,
                1));
        }

        public Task<FullTextIndexResult> BuildIndexAsync(string directoryPath, string? filePatterns = null, CancellationToken ct = default) =>
            Task.FromResult(new FullTextIndexResult(true, 0, 0, 0, null));

        public bool RemoveIndex(string directoryPath) => true;
    }
}
