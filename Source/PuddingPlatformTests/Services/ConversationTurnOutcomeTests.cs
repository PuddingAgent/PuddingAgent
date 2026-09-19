using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

[TestClass]
public class ConversationTurnOutcomeTests
{
    [TestMethod]
    public void FailedWithoutReply_PreservesCanonicalError()
    {
        var outcome = AgentConversationProjectionService.ProjectTurnOutcome(new ConversationEventEntity
        {
            Type = "turn.failed",
            Payload = """{"errorCode":"runtime_execution_failed","errorMessage":"Visual inputs require a workspace and a vision-capable route.","reply":null}""",
        });
        Assert.AreEqual("failed", outcome.Status);
        Assert.AreEqual("runtime_execution_failed", outcome.ErrorCode);
        StringAssert.Contains(outcome.ErrorMessage!, "Visual inputs");
    }

    [TestMethod]
    [DataRow("turn.failed", "failed")]
    [DataRow("turn.cancelled", "cancelled")]
    public void MalformedPayload_DoesNotHideTerminalState(string type, string expected)
    {
        var outcome = AgentConversationProjectionService.ProjectTurnOutcome(new ConversationEventEntity { Type = type, Payload = "{" });
        Assert.AreEqual(expected, outcome.Status);
        Assert.IsNull(outcome.ErrorMessage);
    }
}
