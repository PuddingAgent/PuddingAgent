// ADR-089 U0-S3：FileSearchTool 统一覆盖合同接入 + 「Everything 清单遗漏新文件」差分基线测试。
// 设计依据：Docs/Features/Agent统一检索与渐进展开工具链设计-2026-09-13.md
//   L128：专门验证"Everything 清单遗漏新文件"不被误报为 no_match；基线与索引辅助路径同一匹配合同。
// 覆盖声明单点产出用计数断言锁定（U0-S2 教训：Contains 断言锁不住"同一声明输出两遍"，见 ff79f3b）。
using System.Text.Json;
using PuddingCode.Tools;
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
