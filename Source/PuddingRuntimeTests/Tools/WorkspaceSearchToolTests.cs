using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;
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

    private sealed class FakeRefSource(string? contentHash, IReadOnlyList<SymbolRefRelocation> candidates) : ISymbolRefSource
    {
        public int HashCalls { get; private set; }

        public Task<string?> GetFileContentHashAsync(
            string workspaceId, string projectId, string filePath, CancellationToken cancellationToken)
        {
            HashCalls++;
            return Task.FromResult(contentHash);
        }

        public Task<IReadOnlyList<SymbolRefRelocation>> FindSymbolCandidatesAsync(
            string workspaceId, string projectId, string symbolId, CancellationToken cancellationToken) =>
            Task.FromResult(candidates);

        public Task<string?> GetProjectRootAsync(
            string workspaceId, string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(@"D:\repo");
    }

    private const string RefHash = "ABCDEF0123456789";

    private static string RefToken(string symbolId = "T:Proj.Order") =>
        SymbolRefV1.Create("proj-1", "Source/Proj/Order.cs", symbolId, 10, 20, RefHash, "codeindex-v1").ToToken();

    [TestMethod]
    public async Task SymbolRef_ResolvesToAnchor_AndSendsNoQuery()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.GenuinelyAbsent, RetrievalNextStep.AcceptAbsence("x"), "idx"));
        var tool = new WorkspaceSearchTool(port, new FakeRefSource(RefHash, []));

        var result = await ExecuteAsync(tool, $$"""{ "symbolRef": "{{RefToken()}}", "intent": "callers" }""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(port.LastRequest);

        // 关键：主体是锚点，查询词为空 —— 目标由身份而非名字确定。
        Assert.IsTrue(port.LastRequest!.HasAnchor);
        Assert.AreEqual(string.Empty, port.LastRequest.Query);
        Assert.AreEqual("proj-1", port.LastRequest.Anchor!.ProjectId);
        SymbolRefV1.TryParse(RefToken(), out var expected, out _);
        Assert.AreEqual(expected!.SymbolId, port.LastRequest.Anchor.SymbolId);
        Assert.AreEqual(RetrievalIntent.Callers, port.LastRequest.Intent);
    }

    [TestMethod]
    public async Task StaleSymbolRef_FailsWithHint_AndNeverReachesDataPlane()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.GenuinelyAbsent, RetrievalNextStep.AcceptAbsence("x"), "idx"));
        // 索引里的 hash 与 ref 不同 ⇒ 源版本已变化，不得沿旧行号/旧身份继续。
        var tool = new WorkspaceSearchTool(port, new FakeRefSource("9999999999", []));

        var result = await ExecuteAsync(tool, $$"""{ "symbolRef": "{{RefToken()}}", "intent": "callers" }""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
        StringAssert.Contains(result.Error, "stale");
        Assert.IsNull(port.LastRequest, "陈旧 ref 不得被转成任何检索请求");
    }

    [TestMethod]
    public async Task UnwiredRefSource_FailsExplicitly_InsteadOfFallingBackToNameSearch()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.GenuinelyAbsent, RetrievalNextStep.AcceptAbsence("x"), "idx"));
        var tool = new WorkspaceSearchTool(port);

        var result = await ExecuteAsync(tool, $$"""{ "symbolRef": "{{RefToken()}}", "intent": "callers" }""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
        StringAssert.Contains(result.Error, "not wired");
        Assert.IsNull(port.LastRequest, "未接线时必须显式拒绝，不得静默改成按名字检索");
    }

    [TestMethod]
    public async Task QueryAndSymbolRefTogether_AreRejected()
    {
        var port = new FakePort(r => RetrievalResult.Empty(
            r, RetrievalEmptyReason.GenuinelyAbsent, RetrievalNextStep.AcceptAbsence("x"), "idx"));
        var tool = new WorkspaceSearchTool(port, new FakeRefSource(RefHash, []));

        var result = await ExecuteAsync(
            tool, $$"""{ "query": "Order", "symbolRef": "{{RefToken()}}", "intent": "callers" }""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
        StringAssert.Contains(result.Error, "mutually exclusive");
        Assert.IsNull(port.LastRequest);
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
