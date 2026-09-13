// ADR-089 U0-S3：FileSearchTool 统一覆盖合同接入 + 「Everything 清单遗漏新文件」差分基线测试。
// 设计依据：Docs/Features/Agent统一检索与渐进展开工具链设计-2026-09-13.md
//   L128：专门验证"Everything 清单遗漏新文件"不被误报为 no_match；基线与索引辅助路径同一匹配合同。
// 覆盖声明单点产出用计数断言锁定（U0-S2 教训：Contains 断言锁不住"同一声明输出两遍"，见 ff79f3b）。
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Tools;
using PuddingFullTextIndex.Contracts;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Search;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class FileSearchToolTests
{
    // T1（设计 L128 核心门禁）：Everything 清单故意漏掉磁盘上真实存在的新文件时，
    // 差分补足必须找回该文件，且不得误报 no_match，覆盖状态非 Complete。
    [TestMethod]
    public async Task Everything_Manifest_Missing_NewFile_Is_Recovered_By_Differential_Enumeration()
    {
        var root = CreateTempDir("u0s3-diff-");
        try
        {
            var indexed = await WriteFileAsync(root, "indexed-file.txt");
            var brandNew = await WriteFileAsync(root, "brand-new-file.txt");

            // 伪造清单只含旧文件，漏掉 brand-new-file.txt（真实存在），清单不可自证完整。
            var sdk = new StubEverythingSdk([new EverythingQueryItem(indexed)]);
            var builtIn = new CountingBuiltInProvider();
            var tool = new FileSearchTool([new EverythingSearchProvider(sdk), builtIn]);

            var result = await ExecuteAsync(tool, $$"""
            {
              "directory": "{{JsonEscape(root)}}",
              "pattern": "*.txt",
              "recursive": true
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "brand-new-file.txt", "differential enumeration must recover the missed file");
            StringAssert.Contains(result.Output, "indexed-file.txt");
            Assert.AreNotEqual(ToolResultStatuses.NoMatch, result.Status, "missing new file must NOT be reported as no_match");
            StringAssert.Contains(result.Output, "missed 1 file", "coverage note must disclose the detected manifest gap");
            Assert.AreEqual(1, CountOccurrences(result.Output, "(coverage: partial"), "coverage note must appear exactly once");
            Assert.AreEqual(1, builtIn.SearchCallCount, "differential enumeration must run for unverifiable manifest");
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // T2：清单被声明完整（覆盖与时效可验证）→ 直接定案 Complete，不重复扫描。
    [TestMethod]
    public async Task Complete_Manifest_Skips_Differential_Enumeration_And_Reports_Complete()
    {
        var root = CreateTempDir("u0s3-complete-");
        try
        {
            var indexed = await WriteFileAsync(root, "indexed-file.txt");
            var sdk = new StubEverythingSdk([new EverythingQueryItem(indexed)], isCompleteManifest: true);
            var builtIn = new CountingBuiltInProvider();
            var tool = new FileSearchTool([new EverythingSearchProvider(sdk), builtIn]);

            var result = await ExecuteAsync(tool, $$"""
            {
              "directory": "{{JsonEscape(root)}}",
              "pattern": "*.txt",
              "recursive": true
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "indexed-file.txt");
            Assert.AreEqual(0, builtIn.SearchCallCount, "verifiably complete manifest must not trigger differential enumeration");
            // Complete 契约（对齐 U0-S2 SearchGrepTool）：无覆盖声明行 + status ok。
            Assert.AreEqual(0, CountOccurrences(result.Output, "(coverage:"), "complete coverage emits no coverage note");
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // T3：命中数达到 maxResults → 截断必须显式声明且状态为 truncated（不得当作完整结果）。
    [TestMethod]
    public async Task Everything_Hitting_Result_Limit_Reports_Truncated()
    {
        var root = CreateTempDir("u0s3-trunc-");
        try
        {
            await WriteFileAsync(root, "a-file.txt");
            await WriteFileAsync(root, "b-file.txt");
            await WriteFileAsync(root, "c-file.txt");

            // SDK 层原始条目达 maxResults 上限（Everything_SetMax 截断证据）。
            var sdk = new StubEverythingSdk(
            [
                new EverythingQueryItem(Path.Combine(root, "a-file.txt")),
                new EverythingQueryItem(Path.Combine(root, "b-file.txt")),
            ], isCompleteManifest: true);
            var builtIn = new CountingBuiltInProvider();
            var tool = new FileSearchTool([new EverythingSearchProvider(sdk), builtIn]);

            var result = await ExecuteAsync(tool, $$"""
            {
              "directory": "{{JsonEscape(root)}}",
              "pattern": "*.txt",
              "recursive": true,
              "max_results": 2
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(ToolResultStatuses.Truncated, result.Status, "hit-limit results must not be reported as complete");
            Assert.AreEqual(1, CountOccurrences(result.Output, "(coverage: truncated"), "truncation must be declared exactly once");
            StringAssert.Contains(result.Output, "a-file.txt");
            StringAssert.Contains(result.Output, "b-file.txt");
            Assert.AreEqual(0, builtIn.SearchCallCount, "top-up cannot change the coverage verdict once the result set is full");
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // T4：Everything 不可用 → 回退内置枚举仍然可用，且覆盖声明存在并恰好一次（含 fallback 声明）。
    [TestMethod]
    public async Task Everything_Unavailable_Falls_Back_With_Single_Coverage_Declaration()
    {
        var root = CreateTempDir("u0s3-fallback-");
        try
        {
            await WriteFileAsync(root, "real-file.txt");
            var sdk = new StubEverythingSdk([], available: false);
            var builtIn = new CountingBuiltInProvider();
            var tool = new FileSearchTool([new EverythingSearchProvider(sdk), builtIn]);

            // auto 模式：Everything 不可用 → 内置枚举接管（D-f：降级必须有声明，不得静默）。
            var result = await ExecuteAsync(tool, $$"""
            {
              "directory": "{{JsonEscape(root)}}",
              "pattern": "*.txt",
              "recursive": true
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "real-file.txt");
            StringAssert.Contains(result.Output, "Provider fallback: Everything -> BuiltInRecursiveFileSearch");
            // Complete 契约：无覆盖声明行；fallback 声明（降级说明）恰好一次。
            Assert.AreEqual(0, CountOccurrences(result.Output, "(coverage:"), "complete coverage emits no coverage note");
            Assert.AreEqual(1, CountOccurrences(result.Output, "Provider fallback:"), "fallback declaration must appear exactly once");
            Assert.AreEqual(ToolResultStatuses.Ok, result.Status);
            Assert.AreEqual(1, builtIn.SearchCallCount);
            Assert.AreEqual(0, sdk.QueryCallCount);
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // T5a：清单不可自证但差分枚举自然完成且确无匹配 → Complete，此时 no_match 才合法。
    [TestMethod]
    public async Task Empty_Manifest_With_Empty_Scope_Concludes_Verified_NoMatch()
    {
        var root = CreateTempDir("u0s3-empty-");
        try
        {
            var sdk = new StubEverythingSdk([]);
            var tool = new FileSearchTool([new EverythingSearchProvider(sdk), new CountingBuiltInProvider()]);

            var result = await ExecuteAsync(tool, $$"""
            {
              "directory": "{{JsonEscape(root)}}",
              "pattern": "*.txt",
              "recursive": true
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "[]");
            StringAssert.Contains(result.Output, "No files matched");
            Assert.AreEqual(ToolResultStatuses.NoMatch, result.Status, "no_match is only legal when the scope was fully enumerated");
            Assert.AreEqual(0, CountOccurrences(result.Output, "(coverage:"), "complete coverage emits no coverage note");
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // T5b：清单不可自证且差分枚举失败 → Partial + 空结果，不得输出 no_match / "No files matched"。
    [TestMethod]
    public async Task NonComplete_Coverage_Never_Reports_NoMatch()
    {
        var root = CreateTempDir("u0s3-partial-");
        try
        {
            var sdk = new StubEverythingSdk([]);
            var tool = new FileSearchTool([new EverythingSearchProvider(sdk), new ThrowingBuiltInProvider()]);

            var result = await ExecuteAsync(tool, $$"""
            {
              "directory": "{{JsonEscape(root)}}",
              "pattern": "*.txt",
              "recursive": true
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreNotEqual(ToolResultStatuses.NoMatch, result.Status);
            Assert.IsFalse(result.Output.Contains("No files matched"), "incomplete coverage must never claim no-match");
            Assert.AreEqual(1, CountOccurrences(result.Output, "(coverage: partial"), "coverage note must appear exactly once");
            StringAssert.Contains(result.Output, "differential enumeration failed");
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // T6：require_provider=true 时尊重单一 provider 语义——不差分，但保持 Partial 诚实上报。
    [TestMethod]
    public async Task RequireProvider_Keeps_Partial_Without_Differential_Enumeration()
    {
        var root = CreateTempDir("u0s3-require-");
        try
        {
            var indexed = await WriteFileAsync(root, "indexed-file.txt");
            var sdk = new StubEverythingSdk([new EverythingQueryItem(indexed)]);
            var builtIn = new CountingBuiltInProvider();
            var tool = new FileSearchTool([new EverythingSearchProvider(sdk), builtIn]);

            var result = await ExecuteAsync(tool, $$"""
            {
              "provider": "Everything",
              "require_provider": true,
              "directory": "{{JsonEscape(root)}}",
              "pattern": "*.txt",
              "recursive": true
            }
            """);

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "indexed-file.txt");
            Assert.AreEqual(0, builtIn.SearchCallCount, "require_provider must opt out of differential enumeration");
            Assert.AreNotEqual(ToolResultStatuses.NoMatch, result.Status, "unverifiable manifest must not yield no_match");
            Assert.AreEqual(1, CountOccurrences(result.Output, "(coverage: partial"), "coverage note must appear exactly once");
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // ===== ADR-089 U0-G3：FileSearchTool 通配分支统一到共享 glob 合同（RetrievalGlobMatcher）=====

    // G3-1：canonical glob 合同下 *.txt 不得命中 b.txtx（反证 Win32 searchPattern 前导匹配怪癖）。
    [TestMethod]
    public async Task FileSearch_Glob_WithTxt_Excludes_Atxtx()
    {
        var root = CreateTempDir("u0g3-wtxt-");
        try
        {
            await WriteFileAsync(root, "a.txt");
            await WriteFileAsync(root, "b.txtx");

            var result = await RunBuiltInFileSearchAsync(root, "*.txt");

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "a.txt");
            Assert.IsFalse(result.Output.Contains("b.txtx"), "glob *.txt must not match b.txtx (Win32 leading-match quirk)");
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // G3-2：规范 8——* 不跨路径分隔符。目录名 "sub" 不得借 s* 跨过 / 命中其中文件
    // （旧 GlobLikeMatch 相对路径分支会把 * 展开到整个相对路径，形成跨目录误配）；
    // 而 *a.cs 命中 sub/a.cs 由文件名规则决定（文件名 a.cs 匹配），与 sub/ 前缀无关。
    [TestMethod]
    public async Task FileSearch_Glob_Star_Does_Not_Cross_Directory_Separator()
    {
        var root = CreateTempDir("u0g3-star-");
        try
        {
            await WriteFileAsync(root, "single.txt");
            await WriteFileAsync(root, "plain.txt");
            await WriteFileAsync(root, "a.cs");
            await WriteFileInSubDirAsync(root, "sub/a.cs");

            var starOnly = await RunBuiltInFileSearchAsync(root, "s*");
            Assert.IsTrue(starOnly.Success, starOnly.Error);
            StringAssert.Contains(starOnly.Output, "single.txt");
            Assert.IsFalse(starOnly.Output.Contains("a.cs"), "s* must not cross '/' and match sub/a.cs (canonical rule 8)");
            Assert.IsFalse(starOnly.Output.Contains("plain.txt"));

            var endsWithAcs = await RunBuiltInFileSearchAsync(root, "*a.cs");
            Assert.IsTrue(endsWithAcs.Success, endsWithAcs.Error);
            StringAssert.Contains(endsWithAcs.Output, "a.cs");
            Assert.IsFalse(endsWithAcs.Output.Contains("single.txt"));
            Assert.IsFalse(endsWithAcs.Output.Contains("plain.txt"));
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // G3-3：含分隔符 glob 按相对路径匹配（规范 5），且 * 不跨 /（规范 8）——
    // sub/*.cs 命中子目录直接子文件，不命中更深层 nested，也不命中根目录同名扩展。
    [TestMethod]
    public async Task FileSearch_Glob_WithDirectory_Matches_RelativePath_Without_Crossing()
    {
        var root = CreateTempDir("u0g3-dir-");
        try
        {
            await WriteFileAsync(root, "a.cs");
            await WriteFileInSubDirAsync(root, "sub/a.cs");
            await WriteFileInSubDirAsync(root, "sub/nested/b.cs");

            var result = await RunBuiltInFileSearchAsync(root, "sub/*.cs");

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "a.cs");
            Assert.IsFalse(result.Output.Contains("b.cs"), "sub/*.cs must not cross '/' into nested/b.cs (canonical rule 8)");
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // G3-4（父级决策显式登记）：非通配 pattern = 大小写不敏感子串包含，不是 glob 合同。
    // "logo" 命中 logo.png 与 sub/logo-icon.png；该契约在 U1 拆参（pattern 文本 vs glob 过滤器）前保持不变。
    [TestMethod]
    public async Task FileSearch_NonGlob_Substring_Semantics_Is_Explicitly_Preserved()
    {
        var root = CreateTempDir("u0g3-substr-");
        try
        {
            await WriteFileAsync(root, "logo.png");
            await WriteFileInSubDirAsync(root, "sub/logo-icon.png");
            await WriteFileAsync(root, "unrelated.txt");

            var result = await RunBuiltInFileSearchAsync(root, "logo");

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains(result.Output, "logo.png");
            StringAssert.Contains(result.Output, "logo-icon.png");
            Assert.IsFalse(result.Output.Contains("unrelated.txt"));
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // G3-5（S3）：legacy 非覆盖 SearchAsync 不再把 pattern 交给 Win32 searchPattern——
    // 统一 "*" 枚举 + FileSearchPatternMatcher 过滤；*.txt 不得命中 b.txtx，TopDirectoryOnly 语义不变。
    [TestMethod]
    public async Task Legacy_SearchAsync_Uses_Unified_Matcher()
    {
        var root = CreateTempDir("u0g3-legacy-");
        try
        {
            await WriteFileAsync(root, "a.txt");
            await WriteFileAsync(root, "b.txtx");
            await WriteFileInSubDirAsync(root, "sub/c.txt");
            var provider = new BuiltInRecursiveFileSearchProvider();

            var recursive = await provider.SearchAsync(root, "*.txt", recursive: true, maxResults: 50, ct: CancellationToken.None);
            CollectionAssert.AreEquivalent(
                new[] { Path.Combine(root, "a.txt"), Path.Combine(root, "sub", "c.txt") },
                recursive.ToArray());

            var topOnly = await provider.SearchAsync(root, "*.txt", recursive: false, maxResults: 50, ct: CancellationToken.None);
            CollectionAssert.AreEquivalent(new[] { Path.Combine(root, "a.txt") }, topOnly.ToArray());
        }
        finally
        {
            DeleteDir(root);
        }
    }

    // G3-6：跨工具奇偶校验——同一目录、同一 glob 下，FileSearchTool（内置枚举路径）与
    // SearchGrepTool（G2 后同一 glob 合同）的命中文件集合必须一致（含 Win32 陷阱文件 b.txtx）。
    [TestMethod]
    public async Task FileSearch_Glob_CrossTool_Parity_With_SearchGrepTool()
    {
        var root = CreateTempDir("u0g3-parity-");
        try
        {
            foreach (var name in new[] { "a.txt", "Keep1.txt", "b.txtx" })
                await WriteFileAsync(root, name, "PARITYNEEDLE");
            await WriteFileInSubDirAsync(root, "sub/a.cs", "PARITYNEEDLE");
            await WriteFileInSubDirAsync(root, "sub/Keep2.cs", "PARITYNEEDLE");

            var candidates = new[] { "a.txt", "Keep1.txt", "b.txtx", "a.cs", "Keep2.cs" };
            foreach (var glob in new[] { "*.txt", "Keep*.txt", "sub/*.cs" })
            {
                var fileSearch = await RunBuiltInFileSearchAsync(root, glob);
                Assert.IsTrue(fileSearch.Success, fileSearch.Error);

                var grep = await ExecuteGrepAsync("PARITYNEEDLE", root, glob);
                Assert.IsTrue(grep.Success, grep.Error);

                // 集合相等断言：对全部候选文件名逐一做 iff 检查（任一工具单侧命中即失败）。
                foreach (var name in candidates)
                {
                    var inFileSearch = fileSearch.Output.Contains(name);
                    var inGrep = grep.Output.Contains(name + ":");
                    Assert.AreEqual(inGrep, inFileSearch,
                        $"glob '{glob}': cross-tool parity broken for '{name}' (FileSearch={inFileSearch}, SearchGrep={inGrep})");
                }
            }
        }
        finally
        {
            DeleteDir(root);
        }
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "temp", prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string> WriteFileAsync(string root, string fileName)
    {
        var path = Path.Combine(root, fileName);
        await File.WriteAllTextAsync(path, "stub content");
        return path;
    }

    /// <summary>G3：写指定内容的文件（跨工具奇偶校验用例需要命中行文本）。</summary>
    private static async Task<string> WriteFileAsync(string root, string fileName, string content)
    {
        var path = Path.Combine(root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    /// <summary>G3：在子目录中写文件（相对路径可含子目录段，自动建目录）。</summary>
    private static async Task<string> WriteFileInSubDirAsync(string root, string relativePath, string content = "stub content")
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    /// <summary>G3：仅挂内置枚举 provider，直接走统一匹配合同路径（覆盖路径与差分逻辑共用同一 matcher）。</summary>
    private static Task<ToolExecutionResult> RunBuiltInFileSearchAsync(string root, string pattern, bool recursive = true) =>
        ExecuteAsync(new FileSearchTool([new BuiltInRecursiveFileSearchProvider()]), $$"""
        {
          "directory": "{{JsonEscape(root)}}",
          "pattern": "{{JsonEscape(pattern)}}",
          "recursive": {{(recursive ? "true" : "false")}}
        }
        """);

    /// <summary>G3：SearchGrepTool 调用辅助（本地全文索引桩 → managed grep 真实文件系统枚举）。</summary>
    private static Task<ToolExecutionResult> ExecuteGrepAsync(string query, string directory, string pattern) =>
        new SearchGrepTool(NullLogger<SearchGrepTool>.Instance, new StubFullTextSearchEngine(false, null!))
            .ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "call-g3-parity",
                ArgumentsJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["query"] = query,
                    ["directory"] = directory,
                    ["pattern"] = pattern,
                    ["max_results"] = "50",
                }),
                Context = Context(),
            });

    /// <summary>G3：全文索引桩（hasIndex=false → SearchGrepTool 走真实文件系统 managed grep）。</summary>
    private sealed class StubFullTextSearchEngine(bool hasIndex, FullTextSearchResult searchResult) : IFullTextSearchEngine
    {
        public bool HasIndex(string directoryPath) => hasIndex;

        public Task<FullTextSearchResult> SearchAsync(
            string query,
            string directoryPath,
            int maxResults = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default) =>
            Task.FromResult(searchResult);

        public Task<FullTextIndexResult> BuildIndexAsync(
            string directoryPath,
            string? filePatterns = null,
            CancellationToken ct = default) =>
            Task.FromResult(new FullTextIndexResult(true, 0, 0, 0, null));

        public bool RemoveIndex(string directoryPath) => true;
    }

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static string JsonEscape(string value) => JsonSerializer.Serialize(value)[1..^1];

    // 覆盖声明"恰好一次"的计数断言（U0-S2 教训：Contains 只能证明存在，锁不住重复）。
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
    };

    private static Task<ToolExecutionResult> ExecuteAsync(FileSearchTool tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = Context(),
        });

    /// <summary>Everything SDK 桩：可声明清单完整性（真实 Everything64.dll 无该自证出口）。</summary>
    private sealed class StubEverythingSdk(
        IReadOnlyList<EverythingQueryItem> items,
        bool available = true,
        bool isCompleteManifest = false) : IEverythingSdk
    {
        public int QueryCallCount { get; private set; }

        public bool IsAvailable(out string? error)
        {
            error = available ? null : "stub: everything unavailable";
            return available;
        }

        public Task<EverythingQueryResult> QueryAsync(EverythingQueryRequest request, CancellationToken ct)
        {
            QueryCallCount++;
            return Task.FromResult(new EverythingQueryResult(items, isCompleteManifest));
        }
    }

    /// <summary>内置基线计数器：委托真实枚举实现，只为断言"差分是否发生/未发生"。</summary>
    private sealed class CountingBuiltInProvider : IFileSearchProvider
    {
        private readonly BuiltInRecursiveFileSearchProvider _inner = new();

        public string ProviderId => _inner.ProviderId;
        public string DisplayName => _inner.DisplayName;
        public bool IsAvailable => true;
        public int SearchCallCount { get; private set; }

        public Task<IReadOnlyList<string>> SearchAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct) =>
            _inner.SearchAsync(directory, pattern, recursive, maxResults, ct);

        public async Task<FileSearchProviderResult> SearchWithCoverageAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct)
        {
            SearchCallCount++;
            return await _inner.SearchWithCoverageAsync(directory, pattern, recursive, maxResults, ct);
        }
    }

    /// <summary>差分枚举失败桩：模拟基线枚举 IO 中断。</summary>
    private sealed class ThrowingBuiltInProvider : IFileSearchProvider
    {
        public string ProviderId => "BuiltInRecursiveFileSearch";
        public string DisplayName => "throwing builtin";
        public bool IsAvailable => true;

        public Task<IReadOnlyList<string>> SearchAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct) =>
            throw new IOException("stub enumeration failure");

        public Task<FileSearchProviderResult> SearchWithCoverageAsync(string directory, string pattern, bool recursive, int maxResults, CancellationToken ct) =>
            throw new IOException("stub enumeration failure");
    }
}
