using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentExecutionObservabilityAttributionTests
{
    [TestMethod]
    public void CreateExecutionTrace_SubAgent_UsesCanonicalExecutionIdentity()
    {
        var request = CreateRequest(RuntimeExecutionKind.SubAgent);

        var trace = AgentExecutionService.CreateExecutionTrace(request);

        Assert.AreEqual("trace-1", trace.TraceId);
        Assert.AreEqual("run-child", trace.ExecutionId);
        Assert.AreEqual("run-parent", trace.ParentExecutionId);
        Assert.AreEqual("session-child", trace.SubAgentId);
        Assert.AreEqual("session-child", trace.SessionId);
    }

    [TestMethod]
    public void BuildTokenUsageAttribution_SubAgent_PreservesRoundToolsAndParent()
    {
        var request = CreateRequest(RuntimeExecutionKind.SubAgent);

        var attribution = AgentExecutionService.BuildTokenUsageAttribution(
            request,
            round: 4,
            canonicalToolNames: ["search_grep", "shell"]);

        Assert.AreEqual("session-parent", attribution.ParentSessionId);
        Assert.AreEqual("session-child", attribution.SubAgentId);
        Assert.AreEqual(4, attribution.TurnRound);
        Assert.AreEqual(2, attribution.ToolCallCount);
        CollectionAssert.AreEqual(
            new[] { "search_grep", "shell" },
            attribution.ToolNames.ToArray());
    }

    [TestMethod]
    public void BuildTokenUsageAttribution_Main_DoesNotInferSubAgentFromSessionText()
    {
        var request = CreateRequest(RuntimeExecutionKind.ConversationTurn) with
        {
            SessionId = "looks-like-sub-session-sub-deadbeef",
        };

        var attribution = AgentExecutionService.BuildTokenUsageAttribution(
            request,
            round: 0,
            canonicalToolNames: []);
        var trace = AgentExecutionService.CreateExecutionTrace(request);

        Assert.IsNull(attribution.ParentSessionId);
        Assert.IsNull(attribution.SubAgentId);
        Assert.IsNull(trace.SubAgentId);
    }

    private static RuntimeDispatchRequest CreateRequest(RuntimeExecutionKind kind) => new()
    {
        SessionId = "session-child",
        WorkspaceId = "workspace-1",
        AgentTemplateId = "workspace-task-agent",
        AgentInstanceId = "agent-1",
        MessageText = "test",
        ExecutionIdentity = new RuntimeExecutionIdentity
        {
            Kind = kind,
            ConversationId = "session-parent",
            RunId = "run-child",
            ParentRunId = "run-parent",
            TraceId = "trace-1",
        },
    };
}
