using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class LlmRequestTextBudgetTests
{
    private static RuntimeDispatchRequest Request(string provider = "deepseek", string protocol = "responses",
        LlmConfig? config = null) => new()
    {
        SessionId = "size-test", WorkspaceId = "test", AgentTemplateId = "test", MessageText = "continue",
        CallerLlmSnapshot = new(provider, "deepseek-flash", protocol, []),
        LlmConfig = config,
    };

    private static LlmConfig Config(int maxContextTokens, int maxOutputTokens = 8192) => new()
    {
        ModelId = "deepseek-flash", MaxContextTokens = maxContextTokens, MaxOutputTokens = maxOutputTokens,
    };

    [TestMethod]
    public void TextBudget_ScalesWithInputWindow_AndKeepsLegacyFloorAsMinimum()
    {
        // 2026-10-23：固定 512 KiB 预算在 1M 窗口下被固定前缀占满 98.6%（历史只剩约 6 KB），
        // 每轮都触发压缩 ⇒ 预算必须随窗口伸缩，小窗口仍以 512 KiB 为下限。
        var smallLimit = LlmRequestTextBudget.ResolveLimit(Request(config: Config(100_000)));
        var largeLimit = LlmRequestTextBudget.ResolveLimit(Request(config: Config(1_000_000)));
        Assert.AreEqual(LlmRequestTextBudget.MinimumTextBudgetBytes, smallLimit, "小窗口必须保持旧下限");
        Assert.IsTrue(largeLimit > LlmRequestTextBudget.MinimumTextBudgetBytes,
            $"1M 窗口的文本预算应高于旧下限，实际 {largeLimit}");
        Assert.IsTrue(largeLimit >= 2_000_000, $"1M 窗口的文本预算应随窗口成倍放大，实际 {largeLimit}");
    }

    [TestMethod]
    public void LargeWindow_FixedPrefixSizedRequest_IsNoLongerRejected()
    {
        // 固定前缀（系统提示 + 工具定义 + 技能目录）实测约 506 KB：大窗口下它必须能安全上传，
        // 否则压缩永远无法把请求压到预算内（压缩只动历史尾部，动不了固定前缀）。
        var request = Request(config: Config(1_000_000));
        var limit = LlmRequestTextBudget.ResolveLimit(request);
        var fixedPrefixSized = new string('x', 600_000);
        Assert.IsNull(LlmRequestTextBudget.Validate(request, [new(ChatRole.User, fixedPrefixSized)], []),
            "大窗口下 600 KB 的请求不应被判超预算");
        // 缩放后的预算仍然是被执行的硬界限。
        var error = LlmRequestTextBudget.Validate(request,
            [new(ChatRole.User, new string('x', limit + 1024))], []);
        Assert.IsNotNull(error, "超出缩放后预算的请求必须被拦截");
        Assert.IsTrue(LlmFailureDiagnostics.TryGet(error, out var cause));
        Assert.AreEqual(limit.ToString(), cause.Evidence["request_text_budget_bytes"]);
    }

    [TestMethod]
    public void ScaledTextBudget_StillTriggersCompaction_WhenHistoryReallyExceedsIt()
    {
        // 缩小窗口但保持“历史真超预算”语义：预算仍高于旧 512 KiB 下限（证明确实随窗口伸缩），
        // 同时控制夹具体积，避免单测被 3 MB 级文本拖成分钟级（2026-10-23）。
        var config = Config(300_000, maxOutputTokens: 1_000);
        var request = Request(config: config);
        var limit = LlmRequestTextBudget.ResolveLimit(request);
        Assert.IsTrue(limit > LlmRequestTextBudget.MinimumTextBudgetBytes, $"预算应随窗口放大，实际 {limit}");
        var perMessage = (int)(limit * 1.15 / 48);
        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        for (var i = 0; i < 48; i++)
            history.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, new string('y', perMessage)));
        var store = new ContextUsageSnapshotStore();
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(store, "size-test", history, history, [], config,
            0.8, 0.5, out var plan, maxRequestTextBytes: limit));
        Assert.IsNotNull(plan);
        Assert.IsTrue(plan.InitialTextBytes > limit, "文本触发线必须仍然生效");
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
        var config = Config(1_000_000);
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(new(), "size-test", history, history, [], config,
            0.8, 0.5, out var plan, maxRequestTextBytes: LlmRequestTextBudget.MinimumTextBudgetBytes));
        Assert.IsNotNull(plan);
        CollectionAssert.AreEqual(new[] { history[0], history[^1] }, plan.RetainedMessages.ToArray());
        Assert.AreEqual(6, history.Count, "Planning never deletes source messages.");
    }

    [TestMethod]
    public void OversizedProtectedMessage_BlocksBeforeDispatchWithDiagnosticEvidence()
    {
        var oversized = new string('x', LlmRequestTextBudget.ResolveLimit(Request()) + 1024);
        var error = LlmRequestTextBudget.Validate(Request(), [new(ChatRole.User, oversized)], [], "summary_rejected");
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
        Assert.AreEqual(0, LlmRequestTextBudget.ResolveLimit(Request("openai", config: Config(1_000_000))));
        Assert.AreEqual(0, LlmRequestTextBudget.ResolveLimit(Request(protocol: "anthropic", config: Config(1_000_000))));
    }

    [TestMethod]
    public void ToolDefinitions_CountTowardUploadBudget()
    {
        var description = new string('x', LlmRequestTextBudget.ResolveLimit(Request()) + 1024);
        var tools = new[] { new LlmToolDefinition
        {
            Name = "large_schema", Description = description,
            Parameters = new ToolParameterSchema([], []),
        } };
        var error = LlmRequestTextBudget.Validate(Request(), [new(ChatRole.User, "small")], tools);
        Assert.IsNotNull(error);
        Assert.IsTrue(LlmFailureDiagnostics.TryGet(error, out var cause));
        Assert.IsTrue(long.Parse(cause.Evidence["tool_definition_bytes"]) >= description.Length);
    }
}
