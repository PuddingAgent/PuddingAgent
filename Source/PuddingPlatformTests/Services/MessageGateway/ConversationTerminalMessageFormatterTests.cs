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
    public void Parse_FailureWithDiagnostic_UsesCauseAndSafeBudgetEvidence()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "Failed", errorCode = "InvalidOperationException", errorMessage = "generic error",
            diagnostic = new
            {
                causeCode = "local.context_compaction_required", causeTitle = "上下文整理未完成",
                causeShortCause = "摘要无法提交，历史仍完整保留。", remediationHint = "查看压缩诊断。",
                errorId = "llm-test", traceId = "trace-test",
                evidenceJson = """{"estimated_request_text_bytes":"957856","request_text_budget_bytes":"524288","tool_definition_bytes":"158675","compaction_reason":"durable_commit_coveredmessagesnotfound","secret":"must-not-render"}""",
            },
        });
        var result = ConversationTerminalMessageFormatter.Parse(payload)!;
        StringAssert.Contains(result.Content, "local.context_compaction_required");
        StringAssert.Contains(result.Content, "durable_commit_coveredmessagesnotfound");
        StringAssert.Contains(result.Content, "llm-test");
        StringAssert.Contains(result.Content, "trace-test");
        Assert.IsFalse(result.Content.Contains("InvalidOperationException"));
        Assert.IsFalse(result.Content.Contains("must-not-render"));
        Assert.AreEqual("上下文整理未完成", result.Summary);
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
