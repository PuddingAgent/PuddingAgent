using PuddingAgent.Services;
using PuddingCode.Platform;

namespace PuddingHost.Tests;

public class DingTalkExecutionRoutingTests
{
    [Theory]
    [InlineData("dingtalk", true, true)]
    [InlineData("dingtalk", false, false)]
    [InlineData("feishu", true, false)]
    [InlineData("web-chat", false, false)]
    public void PublicExecutionRequiresAnUnboundDingTalkSender(string channel, bool unbound, bool expected)
    {
        var origin = new MessageOrigin { FromKind = "user", FromId = "sender", MessageType = "chat",
            ChannelType = channel, IsUnboundChannelSender = unbound };
        Assert.Equal(expected, DingTalkTurnExecutor.RequiresPublicExecution(origin));
        Assert.False(DingTalkTurnExecutor.RequiresPublicExecution(null));
    }
}
