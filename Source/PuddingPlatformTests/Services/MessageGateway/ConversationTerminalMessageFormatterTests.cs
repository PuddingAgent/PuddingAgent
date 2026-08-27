using PuddingPlatform.Services.MessageGateway;

namespace PuddingPlatformTests.Services.MessageGateway;

[TestClass]
public sealed class ConversationTerminalMessageFormatterTests
{
    [TestMethod]
    public void Parse_FailedTerminalWithoutReply_ReturnsActionableDiagnostic()
    {
        var presentation = ConversationTerminalMessageFormatter.Parse(
            """{"kind":"Failed","errorCode":"agent_configuration_invalid","errorMessage":"preferredModelId is invalid","reply":null}""");

        Assert.IsNotNull(presentation);
        Assert.IsTrue(presentation.IsError);
        Assert.AreEqual("请求失败", presentation.Summary);
        StringAssert.Contains(presentation.Content, "agent_configuration_invalid");
        StringAssert.Contains(presentation.Content, "preferredModelId is invalid");
    }

    [TestMethod]
    public void Parse_CompletedTerminalWithoutReply_ReturnsNull()
    {
        Assert.IsNull(ConversationTerminalMessageFormatter.Parse(
            """{"kind":"Completed","errorCode":null,"errorMessage":null,"reply":null}"""));
    }

    [TestMethod]
    public void Parse_SyntheticEmptyReply_ReturnsNull()
    {
        Assert.IsNull(ConversationTerminalMessageFormatter.Parse(
            $$"""{"kind":"Completed","reply":"{{ConversationTerminalMessageFormatter.SyntheticEmptyReply}}"}"""));
    }
}
