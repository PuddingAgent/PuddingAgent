using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class LlmRequestTextBudgetTests
{
    private static RuntimeDispatchRequest Request(string provider = "deepseek", string protocol = "responses") => new()
    {
        SessionId = "size-test", WorkspaceId = "test", AgentTemplateId = "test", MessageText = "continue",
        CallerLlmSnapshot = new(provider, "deepseek-flash", protocol, []),
    };

    [TestMethod]
    public void LargeWindow_StillRequiresCompactionForTextUpload()
    {
        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        for (var i = 0; i < 48; i++)
            history.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                string.Concat(Enumerable.Repeat("Record of pending work, exact paths and user constraints. ", 550))));
        var config = new LlmConfig { ModelId = "deepseek-flash", MaxContextTokens = 1_048_576, MaxOutputTokens = 8192 };
        var store = new ContextUsageSnapshotStore();
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(store, "size-test", history, history, [], config,
            0.8, 0.5, out var plan, maxRequestTextBytes: LlmRequestTextBudget.ResolveLimit(Request())));
        Assert.IsNotNull(plan);
        Assert.IsTrue(plan.InitialUsedTokens < plan.EffectiveInputLimit * 0.8);
        Assert.IsTrue(plan.RequiresSynchronousProtection);
        Assert.IsTrue(plan.RequiresTextProtection);
        Assert.IsTrue(LlmRequestTextBudget.Measure(plan.RetainedMessages, []) < LlmRequestTextBudget.DeepSeekResponsesLimit);
        Assert.IsTrue(history.Count == 49, "Planning must not mutate source history.");
    }

    [TestMethod]
    public void LargeRecentToolExchange_CanBeSummarizedWithoutRemovingCurrentUser()
    {
        var body = string.Concat(Enumerable.Repeat("Record of pending work and exact user constraints. ", 12_000));
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system"), new(ChatRole.User, "previous task"),
            new(ChatRole.Assistant, "tool request", ToolCalls: [new ToolCall("call-1", "read", "{}")]),
            new(ChatRole.Tool, body, ToolCallId: "call-1", ToolName: "read"),
            new(ChatRole.Assistant, "previous answer"), new(ChatRole.User, "current task"),
        };
        var config = new LlmConfig { ModelId = "deepseek-flash", MaxContextTokens = 1_048_576, MaxOutputTokens = 8192 };
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(new(), "size-test", history, history, [], config,
            0.8, 0.5, out var plan, maxRequestTextBytes: LlmRequestTextBudget.DeepSeekResponsesLimit));
        Assert.IsNotNull(plan);
        CollectionAssert.AreEqual(new[] { history[0], history[^1] }, plan.RetainedMessages.ToArray());
        Assert.AreEqual(6, history.Count, "Planning never deletes source messages.");
    }

    [TestMethod]
    public void OversizedProtectedMessage_BlocksBeforeDispatchWithDiagnosticEvidence()
    {
        var error = LlmRequestTextBudget.Validate(Request(), [new(ChatRole.User, new string('x', 600_000))], [], "summary_rejected");
        Assert.IsNotNull(error);
        Assert.IsTrue(LlmFailureDiagnostics.TryGet(error, out var cause));
        Assert.AreEqual("local.context_compaction_required", cause.Code);
        Assert.AreEqual("0", cause.Evidence["dispatch_count"]);
        Assert.AreEqual("true", cause.Evidence["history_preserved"]);
        Assert.IsFalse(cause.Retryable);
        Assert.AreEqual("summary_rejected", cause.Evidence["compaction_reason"]);
    }

    [TestMethod]
    public void SmallRequestAndOtherRoutes_AreUnaffected()
    {
        Assert.IsNull(LlmRequestTextBudget.Validate(Request(), [new(ChatRole.User, "small")], []));
        Assert.AreEqual(0, LlmRequestTextBudget.ResolveLimit(Request("openai")));
        Assert.AreEqual(0, LlmRequestTextBudget.ResolveLimit(Request(protocol: "anthropic")));
    }

    [TestMethod]
    public void ToolDefinitions_CountTowardUploadBudget()
    {
        var tools = new[] { new LlmToolDefinition
        {
            Name = "large_schema", Description = new string('x', 600_000),
            Parameters = new ToolParameterSchema([], []),
        } };
        var error = LlmRequestTextBudget.Validate(Request(), [new(ChatRole.User, "small")], tools);
        Assert.IsNotNull(error);
        Assert.IsTrue(LlmFailureDiagnostics.TryGet(error, out var cause));
        Assert.IsTrue(long.Parse(cause.Evidence["tool_definition_bytes"]) >= 600_000);
    }
}
