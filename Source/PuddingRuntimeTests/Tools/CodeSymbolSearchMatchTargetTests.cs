using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// ADR-089 §2.3 匹配域的**接入面**：工具层必须把 <c>match_target</c> 正确翻译成请求上的匹配域，
/// 且未知取值必须 fail-closed（不得静默当成"全开"去查 —— 那会把"筛错了"伪装成"筛过了"）。
/// 核心检索行为本身由叶子组件测试锁定：<c>PuddingCodeIndexTests/Storage/CodeSymbolMatchTargetTests</c>。
/// </summary>
[TestClass]
public sealed class CodeSymbolSearchMatchTargetTests
{
    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
    };

    private static Task<ToolExecutionResult> ExecuteAsync(CodeSymbolSearchTool tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = Context(),
        });

    [TestMethod]
    public async Task MatchTarget_Is_Passed_Through_To_The_Query()
    {
        var service = new RecordingQueryService();
        var tool = new CodeSymbolSearchTool(service);

        var result = await ExecuteAsync(tool, """{"query":"Conf","match_target":"name"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, service.SearchCallCount);
        Assert.AreEqual(CodeSymbolMatchTarget.Name, service.LastRequest!.MatchTarget);
    }

    [TestMethod]
    public async Task MatchTarget_Defaults_To_All_When_Omitted()
    {
        var service = new RecordingQueryService();
        var tool = new CodeSymbolSearchTool(service);

        var result = await ExecuteAsync(tool, """{"query":"Conf"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(CodeSymbolMatchTarget.All, service.LastRequest!.MatchTarget,
            "缺省匹配域必须是 All —— 现有调用方一行不改、行为逐字不变");
    }

    [TestMethod]
    public async Task MatchTarget_Combination_Is_Unioned()
    {
        var service = new RecordingQueryService();
        var tool = new CodeSymbolSearchTool(service);

        await ExecuteAsync(tool, """{"query":"Conf","match_target":"name, signature"}""");

        Assert.AreEqual(
            CodeSymbolMatchTarget.Name | CodeSymbolMatchTarget.Signature,
            service.LastRequest!.MatchTarget);
    }

    [TestMethod]
    public async Task Unknown_MatchTarget_Fails_Closed_Without_Reaching_The_Service()
    {
        var service = new RecordingQueryService();
        var tool = new CodeSymbolSearchTool(service);

        var result = await ExecuteAsync(tool, """{"query":"Conf","match_target":"bogus"}""");

        Assert.IsFalse(result.Success, "未知匹配域必须显式失败");
        StringAssert.Contains(result.Error, "Unknown match_target 'bogus'");
        Assert.AreEqual(0, service.SearchCallCount,
            "未知匹配域不得回落到'全开'去查：那会把筛错了伪装成筛过了");
    }

    private sealed class RecordingQueryService : ICodeQueryService
    {
        public int SearchCallCount { get; private set; }
        public CodeSymbolSearchRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<CodeSymbolDetail>> SearchSymbolsAsync(
            CodeSymbolSearchRequest request, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            LastRequest = request;
            return Task.FromResult<IReadOnlyList<CodeSymbolDetail>>([]);
        }

        public Task<CodeIndexResult> GetProjectIndexStatusAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolRecord>> ExploreAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRelationRecord>> GetCallersAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRelationRecord>> GetCalleesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CodeSymbolRecord>> GetImpactAsync(
            string workspaceId, string projectId, string symbolId, int maxDepth = 3,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
