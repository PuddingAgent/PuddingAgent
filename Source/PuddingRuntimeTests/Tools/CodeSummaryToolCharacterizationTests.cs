using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>code_summary</c> 的**特征测试**（抽取前先钉行为）。
/// <para>
/// 覆盖外层四条可判定的分支：依赖缺失、参数缺失、符号搜不到、正常输出。
/// 刻意<b>不锁</b>摘要正文的排版与文档注释提取细节 —— 那些是抽取时可能动的部分；
/// 锁住它们会让重构变成"改测试"。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSummaryToolCharacterizationTests
{
    [TestMethod]
    public async Task MissingQueryService_ReportsNotRegistered()
    {
        var tool = new CodeSummaryTool();

        var result = await Run(tool, "OrderService");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "ICodeQueryService is not registered");
    }

    [TestMethod]
    public async Task EmptySymbolName_Fails()
    {
        var tool = NewTool(new FakeQueryService([]));

        var result = await Run(tool, "   ");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "symbol_name is required");
    }

    [TestMethod]
    public async Task SymbolNotFound_ReportsNotFoundWithQueryEcho()
    {
        var tool = NewTool(new FakeQueryService([]));

        var result = await Run(tool, "NoSuchSymbol");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "未找到符号");
        StringAssert.Contains(result.Error, "NoSuchSymbol");
    }

    [TestMethod]
    public async Task FoundSymbol_ReturnsSummaryTextPlusJson()
    {
        var symbol = new CodeSymbolRecord(
            "ws-1", "proj-1", @"D:\repo\OrderService.cs", "sym-1", "OrderService",
            CodeSymbolKind.Class, 10, 40);
        var tool = NewTool(new FakeQueryService([new CodeSymbolDetail(symbol)]));

        var result = await Run(tool, "OrderService");

        Assert.IsTrue(result.Success, result.Error);

        // 输出是「人读文本 + --- + 机器读 JSON」两段（既有形态，抽取后必须保持）。
        StringAssert.Contains(result.Output, "---");

        var jsonPart = SplitJson(result.Output!);
        using var doc = JsonDocument.Parse(jsonPart);
        Assert.AreEqual("sym-1", doc.RootElement.GetProperty("symbol_id").GetString());
        Assert.AreEqual("OrderService", doc.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(
            0,
            doc.RootElement.GetProperty("callers").GetArrayLength(),
            "没有调用者时必须输出空数组，而不是省略字段。");
        Assert.AreEqual(0, doc.RootElement.GetProperty("callees").GetArrayLength());
    }

    private static CodeSummaryTool NewTool(ICodeQueryService query) =>
        new(query, new FixedScopeResolver());

    private static Task<ToolExecutionResult> Run(CodeSummaryTool tool, string symbolName)
    {
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["symbol_name"] = symbolName,
        });

        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "summary-characterization",
            ArgumentsJson = arguments,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-1",
                SessionId = "session",
                AgentInstanceId = "agent",
            },
        });
    }

    private static string SplitJson(string output)
    {
        var index = output.LastIndexOf("---", StringComparison.Ordinal);
        Assert.IsTrue(index >= 0, "输出里必须有分隔线。");
        return output[(index + 3)..];
    }

    private sealed class FakeQueryService(IReadOnlyList<CodeSymbolDetail> hits) : ICodeQueryService
    {
        public Task<CodeIndexResult> GetProjectIndexStatusAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolDetail>> SearchSymbolsAsync(
            CodeSymbolSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(hits);

        public Task<IReadOnlyList<CodeSymbolRecord>> ExploreAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRelationRecord>> GetCallersAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodeRelationRecord>>([]);

        public Task<IReadOnlyList<CodeRelationRecord>> GetCalleesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CodeRelationRecord>>([]);

        public Task<IReadOnlyList<CodeSymbolRecord>> GetImpactAsync(
            string workspaceId, string projectId, string symbolId, int maxDepth = 3,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedScopeResolver : ICodeIndexScopeResolver
    {
        public Task<ScopeResolution?> ResolveAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScopeResolution?>(Build(workspaceId));

        public Task<ScopeResolution> ResolveAndEnsureAsync(
            string workspaceId, string? filePath = null, string? scopePath = null,
            string? scopeId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Build(workspaceId));

        private static ScopeResolution Build(string workspaceId) => new(
            new CodeIndexScope(workspaceId, "proj-1", @"D:\repo", ScopeState.Active, ScopeSource.Auto),
            IsAutoDetected: true,
            IsNewlyCreated: false);
    }
}
