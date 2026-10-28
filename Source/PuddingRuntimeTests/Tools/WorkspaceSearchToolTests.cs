using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="WorkspaceSearchTool"/> 的边界断言。
/// <para>
/// 这是薄壳，所以断言重点不是"能搜到"，而是三条壳必须守住的事：
/// ①非法 intent 必须报错并列出合法值（不得静默退化成 auto）；
/// ②"没搜到"不是工具失败（Success 仍为 true，emptyReason 原样带回）；
/// ③scope 缺省取执行上下文的 WorkspaceId。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceSearchToolTests
{
    private sealed class FakePort(Func<RetrievalRequest, RetrievalResult> factory) : IRetrievalPort
    {
        public RetrievalRequest? LastRequest { get; private set; }

        public Task<RetrievalResult> RetrieveAsync(
            RetrievalRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(factory(request));
        }
    }

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
    };

    private static Task<ToolExecutionResult> ExecuteAsync(WorkspaceSearchTool tool, string argumentsJson) =>
        tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = Context(),
        });

    private static RetrievalHit SampleHit() => new(
        SymbolIdentity.Create("sym-1", @"D:\repo\A.cs", 10),
        "RetrievalPortV1",
        CodeSymbolKind.Class,
        RetrievalHitKind.Declaration,
        new RetrievalEvidence(@"D:\repo\A.cs", 10, 15),
        RetrievalConfidence.Semantic,
        2.0,
        "符号名与查询完全相同（最高置信）",
        RetrievalMatchTarget.SymbolName);

    [TestMethod]
    public async Task MissingQuery_FailsAsContractError()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.GenuinelyAbsent, RetrievalNextStep.AcceptAbsence("x"), "idx"));
        var tool = new WorkspaceSearchTool(port);

        var result = await ExecuteAsync(tool, """{ "intent": "type" }""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
        Assert.IsNull(port.LastRequest, "参数非法时不得调用数据面");
    }

    [TestMethod]
    public async Task UnknownIntent_FailsAndListsValidValues()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.GenuinelyAbsent, RetrievalNextStep.AcceptAbsence("x"), "idx"));
        var tool = new WorkspaceSearchTool(port);

        var result = await ExecuteAsync(tool, """{ "query": "Order", "intent": "callerz" }""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
        StringAssert.Contains(result.Error, "callerz");
        // 合法值必须列出来，否则调用方只能盲猜。
        StringAssert.Contains(result.Error, "callers");
        Assert.IsNull(port.LastRequest);
    }

    [TestMethod]
    public async Task PageSizeOutOfRange_FailsAsContractError()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.GenuinelyAbsent, RetrievalNextStep.AcceptAbsence("x"), "idx"));
        var tool = new WorkspaceSearchTool(port);

        var result = await ExecuteAsync(tool, """{ "query": "Order", "pageSize": 100000 }""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
    }

    [TestMethod]
    public async Task ScopeDefaultsToContextWorkspace_AndIntentIsParsedCaseInsensitively()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.GenuinelyAbsent, RetrievalNextStep.AcceptAbsence("nothing here"), "idx-1"));
        var tool = new WorkspaceSearchTool(port);

        var result = await ExecuteAsync(tool, """{ "query": "Order", "intent": "CALLERS" }""");

        Assert.IsNotNull(port.LastRequest);
        Assert.AreEqual("workspace-1", port.LastRequest!.ScopeId);
        Assert.AreEqual(RetrievalIntent.Callers, port.LastRequest.Intent);

        // "没搜到" 不是工具失败：Success 必须为 true，并带回真实原因。
        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GenuinelyAbsent");
    }

    [TestMethod]
    public async Task ExplicitScopeOverridesContext()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.NotIndexed, RetrievalNextStep.RebuildIndex("rebuild", "scope-x"), "idx-1"));
        var tool = new WorkspaceSearchTool(port);

        await ExecuteAsync(tool, """{ "query": "Order", "scope": "scope-x" }""");

        Assert.AreEqual("scope-x", port.LastRequest!.ScopeId);
    }

    [TestMethod]
    public async Task HitIsSerializedWithEvidenceAndWhy()
    {
        var port = new FakePort(r =>
        {
            var hit = SampleHit();
            return RetrievalResult.Create(r, [hit], 1, "idx-1");
        });
        var tool = new WorkspaceSearchTool(port);

        var result = await ExecuteAsync(tool, """{ "query": "RetrievalPortV1", "intent": "type" }""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "RetrievalPortV1");
        StringAssert.Contains(result.Output, "Declaration");
        StringAssert.Contains(result.Output, "why");
        StringAssert.Contains(result.Output, @"D:\\repo\\A.cs");
        StringAssert.Contains(result.Output, "\"totalCount\":1");
    }
}
