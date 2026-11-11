using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public class ChannelToolInvocationTests
{
    [TestMethod]
    [DataRow("slack", "shell", false)]
    [DataRow("feishu", "plugin_load", false)]
    [DataRow("web-chat", "terminal_start", false)]
    [DataRow("dingtalk", "file_write", false)]
    [DataRow("dingtalk", "mpcal_record_feedback", true)]
    [DataRow("slack", "mpcal_record_feedback", false)]
    [DataRow("slack", "slack_lookup_member", true)]
    public void SenderAuthorizationIsCheckedAtInvocation(string channel, string tool, bool allowed)
    {
        var origin = new MessageOrigin { FromKind = "user", FromId = "sender", MessageType = "chat",
            ChannelType = channel, IsUnboundChannelSender = true };
        Assert.AreEqual(!allowed, AgentExecutionService.DenyGuestToolCall(origin, tool, out var denial));
        if (!allowed) Assert.IsFalse(denial.Success);
        Assert.IsFalse(AgentExecutionService.DenyGuestToolCall(origin with { IsUnboundChannelSender = false }, tool, out _));
    }
}
