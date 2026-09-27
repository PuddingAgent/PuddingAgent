using PuddingCode.Platform;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ConversationProcessMetadataTests
{
    [TestMethod]
    public void DelegationProjectionPreservesSessionAndExecutionIdentitySeparately()
    {
        var entity = new ConversationEventEntity { EventId = "e", Sequence = 8, TurnId = "parent-turn", RunId = "envelope-run",
            Type = ConversationEventTypes.SubAgentRunCreated,
            Payload = """{"run_id":"execution-2","sub_agent_id":"pooled-session","template":"reviewer","task_summary":"inspect","parent_tool_call_id":"call"}""" };
        Assert.IsTrue(AgentConversationProjectionService.TryBuildEventProcessItem(entity, out var item));
        Assert.AreEqual("pooled-session", item.DelegationRunId);
        Assert.AreEqual("execution-2", item.DelegationExecutionId);
        Assert.AreEqual("call", item.ParentToolCallId);
        Assert.AreEqual("parent-turn", item.TurnId);
        entity.Type = ConversationEventTypes.SubAgentRunTimedOut;
        Assert.IsTrue(AgentConversationProjectionService.TryBuildEventProcessItem(entity, out item));
        Assert.AreEqual("error", item.Status); // Preserve Web's existing aggregate status contract.
        Assert.AreEqual("timed_out", item.DelegationStatus);
    }

    [TestMethod]
    public void ToolParentIsProjectedOnlyWhenPresent()
    {
        var entity = new ConversationEventEntity { EventId = "e", Type = ConversationEventTypes.ToolCallRequested,
            Payload = """{"toolCallId":"child","parentToolCallId":"parent","name":"terminal","arguments":"build"}""" };
        Assert.IsTrue(AgentConversationProjectionService.TryBuildEventProcessItem(entity, out var item));
        Assert.AreEqual("child", item.ToolCallId); Assert.AreEqual("parent", item.ParentToolCallId);
        Assert.IsNull(item.DelegationExecutionId);
        entity.Payload = """{"toolCallId":"child","name":"terminal"}""";
        Assert.IsTrue(AgentConversationProjectionService.TryBuildEventProcessItem(entity, out item));
        Assert.IsNull(item.ParentToolCallId);
    }
}
