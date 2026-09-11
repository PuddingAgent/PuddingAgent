using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// S01-B 第二片接线探针：证明请求准备边界冻结的 <see cref="RequestContextAttribution"/>
/// 会随 <see cref="TokenUsageAttribution"/> 传到落账点，provenance = request_prepare_frozen，
/// 且同 session 的后续请求覆盖 store 之后，先前的冻结副本仍保留自己的层指标。
///
/// 说明：本文件只做等价最小探针——Runtime 生产路径的接线是
/// <c>AgentExecutionService.FreezeRequestContext</c>（内部即 RequestContextAttribution.Capture）
/// + <c>AgentExecutionService.BuildTokenUsageAttribution(..., requestContext, ...)</c>，
/// 二者在本文件中被分别断言，组合起来即生产路径的语义。
/// </summary>
[TestClass]
public sealed class RequestAttributionWiringTests
{
    private const string SessionId = "session-wiring-a";

    [TestMethod]
    public void Capture_AtRequestPrepareBoundary_ProducesRequestPrepareFrozenProvenance()
    {
        var (assembly, usage) = SeedStores(
            layerName: "L0-STATIC",
            layerTokens: 111,
            messageTokens: 1111,
            toolDefinitionTokens: 222,
            toolCount: 3);

        var frozen = RequestContextAttribution.Capture(assembly, usage, SessionId);

        Assert.IsNotNull(frozen);
        Assert.AreEqual(RequestContextAttributionSources.RequestPrepareFrozen, frozen!.Source);
        Assert.AreNotEqual(RequestContextAttributionSources.SessionLatestFallback, frozen.Source);
        Assert.AreEqual(SessionId, frozen.SessionId);
        Assert.AreEqual(1, frozen.Layers.Count);
        Assert.AreEqual("L0-STATIC", frozen.Layers[0].LayerName);
        Assert.AreEqual(111, frozen.Layers[0].TokenCount);
        Assert.AreEqual(1111, frozen.MessageTokens);
        Assert.AreEqual(222, frozen.ToolDefinitionTokens);
        Assert.AreEqual(3, frozen.ToolCount);
    }

    [TestMethod]
    public void FrozenAttribution_KeepsOwnLayerMetrics_WhenLaterRequestOverwritesSessionSnapshot()
    {
        var (assembly, usage) = SeedStores("L0-STATIC", 111, messageTokens: 1111, toolDefinitionTokens: 222, toolCount: 3);

        // A 的请求准备边界：冻结
        var frozenA = RequestContextAttribution.Capture(assembly, usage, SessionId);
        Assert.IsNotNull(frozenA);
        Assert.AreEqual(RequestContextAttributionSources.RequestPrepareFrozen, frozenA!.Source);

        // B 随后复用同一 session 覆盖了两个 store（这就是"写入时刻回读最新快照"会读到的内容）
        assembly.Set(new ContextAssemblySnapshot
        {
            SessionId = SessionId,
            AssembledAt = DateTimeOffset.UtcNow.AddSeconds(5),
            Layers = [new ContextLayerInfo { LayerName = "L9-OTHER", TokenCount = 999 }],
        });
        usage.Set(new ContextUsageSnapshot
        {
            SessionId = SessionId,
            RecordedAt = DateTimeOffset.UtcNow.AddSeconds(5),
            MessageTokens = 9999,
            ToolDefinitionTokens = 888,
            ToolCount = 7,
        });

        // A 落账仍保留 A 的层指标与 usage shape（不受 B 覆盖影响）
        Assert.AreEqual("L0-STATIC", frozenA.Layers.Single().LayerName);
        Assert.AreEqual(111, frozenA.Layers.Single().TokenCount);
        Assert.AreEqual(1111, frozenA.MessageTokens);
        Assert.AreEqual(222, frozenA.ToolDefinitionTokens);
        Assert.AreEqual(3, frozenA.ToolCount);

        // 对照组：此刻回读最新快照会得到 B 的 shape —— 正是 session_latest_fallback 语义
        var latest = RequestContextAttribution.Capture(assembly, usage, SessionId);
        Assert.IsNotNull(latest);
        Assert.AreEqual("L9-OTHER", latest!.Layers.Single().LayerName);
        Assert.AreEqual(9999, latest.MessageTokens);
        Assert.AreEqual(888, latest.ToolDefinitionTokens);
        Assert.AreNotEqual(frozenA.MessageTokens, latest.MessageTokens);
        Assert.AreNotEqual(frozenA.Layers.Single().LayerName, latest.Layers.Single().LayerName);
    }

    [TestMethod]
    public void BuildTokenUsageAttribution_CarriesFrozenContextAndInvocationIdentity()
    {
        var (assembly, usage) = SeedStores("L1-TOOLS", 333, messageTokens: 4444, toolDefinitionTokens: 555, toolCount: 12);
        var frozen = RequestContextAttribution.Capture(assembly, usage, SessionId);
        Assert.IsNotNull(frozen);

        var attribution = AgentExecutionService.BuildTokenUsageAttribution(
            CreateRequest(),
            round: 2,
            canonicalToolNames: ["search_grep"],
            requestContext: frozen,
            invocationId: "inv-1",
            attemptId: "inv-1:a0");

        Assert.IsNotNull(attribution.Context);
        Assert.AreEqual(RequestContextAttributionSources.RequestPrepareFrozen, attribution.Context!.Source);
        Assert.AreEqual(1, attribution.Context.Layers.Count);
        Assert.AreEqual("L1-TOOLS", attribution.Context.Layers[0].LayerName);
        Assert.AreEqual(333, attribution.Context.Layers[0].TokenCount);
        Assert.AreEqual(4444, attribution.Context.MessageTokens);
        Assert.AreEqual("inv-1", attribution.InvocationId);
        Assert.AreEqual("inv-1:a0", attribution.AttemptId);

        // 既有语义（round / 工具名 / 会话身份）不被新字段改变
        Assert.AreEqual(2, attribution.TurnRound);
        Assert.AreEqual(1, attribution.ToolCallCount);
        Assert.AreEqual("search_grep", attribution.ToolNames.Single());
    }

    [TestMethod]
    public void BuildTokenUsageAttribution_WithoutFrozenContext_KeepsLegacyFallbackSemantics()
    {
        // 缺省调用（既有调用方与既有测试的形态）：Context/InvocationId/AttemptId 保持空，
        // 记录器因此仍走 session_latest_fallback 回退路径，不被本片改动破坏。
        var attribution = AgentExecutionService.BuildTokenUsageAttribution(
            CreateRequest(),
            round: 0,
            canonicalToolNames: []);

        Assert.IsNull(attribution.Context);
        Assert.IsNull(attribution.InvocationId);
        Assert.IsNull(attribution.AttemptId);
    }

    [TestMethod]
    public void Capture_WithoutAssemblyStore_StillFreezesUsageFacts()
    {
        // ContextAssemblyStore 未注册的 Runtime-only 组合下，仍应冻结可得的 usage 事实，
        // 而不是退化为 session_latest_fallback。
        var usage = new ContextUsageSnapshotStore();
        usage.Set(new ContextUsageSnapshot { SessionId = SessionId, MessageTokens = 42, ToolCount = 1 });

        var frozen = RequestContextAttribution.Capture(null, usage, SessionId);

        Assert.IsNotNull(frozen);
        Assert.AreEqual(RequestContextAttributionSources.RequestPrepareFrozen, frozen!.Source);
        Assert.AreEqual(0, frozen.Layers.Count);
        Assert.AreEqual(42, frozen.MessageTokens);
    }

    [TestMethod]
    public void Capture_WithoutAnySnapshot_ReturnsNullSoRecorderCanReportUnknown()
    {
        Assert.IsNull(RequestContextAttribution.Capture(null, null, SessionId));
        Assert.IsNull(RequestContextAttribution.Capture(new ContextAssemblyStore(), new ContextUsageSnapshotStore(), SessionId));
        Assert.IsNull(RequestContextAttribution.Capture(new ContextAssemblyStore(), new ContextUsageSnapshotStore(), " "));
    }

    private static (ContextAssemblyStore Assembly, ContextUsageSnapshotStore Usage) SeedStores(
        string layerName,
        int layerTokens,
        int messageTokens,
        int toolDefinitionTokens,
        int toolCount)
    {
        var assembly = new ContextAssemblyStore();
        assembly.Set(new ContextAssemblySnapshot
        {
            SessionId = SessionId,
            AssembledAt = DateTimeOffset.UtcNow,
            Layers = [new ContextLayerInfo { LayerName = layerName, TokenCount = layerTokens }],
        });

        var usage = new ContextUsageSnapshotStore();
        usage.Set(new ContextUsageSnapshot
        {
            SessionId = SessionId,
            RecordedAt = DateTimeOffset.UtcNow,
            MessageTokens = messageTokens,
            ToolDefinitionTokens = toolDefinitionTokens,
            ToolCount = toolCount,
        });

        return (assembly, usage);
    }

    private static RuntimeDispatchRequest CreateRequest() => new()
    {
        SessionId = SessionId,
        WorkspaceId = "workspace-1",
        AgentTemplateId = "workspace-task-agent",
        AgentInstanceId = "agent-1",
        MessageText = "test",
        ExecutionIdentity = new RuntimeExecutionIdentity
        {
            Kind = RuntimeExecutionKind.ConversationTurn,
            ConversationId = SessionId,
            RunId = "run-1",
            TraceId = "trace-1",
        },
    };
}
