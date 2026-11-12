using System.Text;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 渠道身份必须出现在「模型看得到」的运行时上下文里，但不得混进用户可见的正文。
/// 这里锁定上下文侧的行为：绑定/未绑定/非渠道三种情形各自的渲染结果。
/// </summary>
[TestClass]
public class ChannelIdentityTailContextTests
{
    [TestMethod]
    public void BoundSender_RendersPuddingIdentityForTheModel()
    {
        var sb = new StringBuilder();

        AgentExecutionService.AppendChannelIdentityLine(sb, new MessageOrigin
        {
            FromKind = "user",
            FromId = "174556385329122125",
            MessageType = "dingtalk.chat",
            ChannelType = "dingtalk",
            SenderPuddingUserId = "admin",
            SenderRole = "admin",
        });

        Assert.AreEqual(
            "[channel-identity channel=dingtalk external=174556385329122125 pudding=admin role=admin]"
            + Environment.NewLine,
            sb.ToString());
    }

    [TestMethod]
    public void UnboundSender_RendersExternalIdOnly()
    {
        var sb = new StringBuilder();

        AgentExecutionService.AppendChannelIdentityLine(sb, new MessageOrigin
        {
            FromKind = "user",
            FromId = "stranger-42",
            MessageType = "feishu.chat",
            ChannelType = "feishu",
        });

        var line = sb.ToString();
        StringAssert.Contains(line, "channel=feishu");
        StringAssert.Contains(line, "external=stranger-42");
        Assert.IsFalse(line.Contains("pudding=", StringComparison.Ordinal));
        Assert.IsFalse(line.Contains("role=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LocalTurnWithoutChannel_RendersNothing()
    {
        var sb = new StringBuilder();

        AgentExecutionService.AppendChannelIdentityLine(sb, new MessageOrigin
        {
            FromKind = "user",
            FromId = "local-user",
            MessageType = "chat",
        });

        Assert.AreEqual(0, sb.Length);
    }
}
