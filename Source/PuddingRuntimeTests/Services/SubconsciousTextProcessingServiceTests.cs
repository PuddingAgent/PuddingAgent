using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubconsciousTextProcessingServiceTests
{
    [TestMethod]
    public async Task SummarizeDailyLogAsync_UsesMemoryLlmConfigAndBuildsConciseIndexPrompt()
    {
        var llm = new RecordingMemoryLlmClient("  ## 今日索引\n- 完成 raw 日志镜像。\n  ");
        var service = new SubconsciousTextProcessingService(llm);
        var config = new MemoryLlmConfig("https://memory.local", "key", "memory-model");

        var result = await service.SummarizeDailyLogAsync(
            new DailyLogSummaryRequest(
                WorkspaceId: "workspace-1",
                AgentInstanceId: "agent-1",
                AgentTemplateId: "template-1",
                Day: "2026-06-16",
                OrdinaryLogMarkdown: "user: 继续\nagent: 已完成 raw log mirror",
                MemoryLlmConfig: config));

        Assert.AreEqual("## 今日索引\n- 完成 raw 日志镜像。", result);
        Assert.AreSame(config, llm.LastConfig);
        StringAssert.Contains(llm.LastSystemPrompt!, "只记录重要工作");
        StringAssert.Contains(llm.LastSystemPrompt!, "精简");
        StringAssert.Contains(llm.LastUserMessage!, "workspace-1");
        StringAssert.Contains(llm.LastUserMessage!, "agent-1");
        StringAssert.Contains(llm.LastUserMessage!, "2026-06-16");
        StringAssert.Contains(llm.LastUserMessage!, "已完成 raw log mirror");
    }

    [TestMethod]
    public async Task SummarizeCurrentSessionAsync_ReturnsEmptyWithoutCallingLlm_WhenInputIsBlank()
    {
        var llm = new RecordingMemoryLlmClient("should not be used");
        var service = new SubconsciousTextProcessingService(llm);

        var result = await service.SummarizeCurrentSessionAsync(
            new CurrentSessionSummaryRequest(
                WorkspaceId: "workspace-1",
                AgentInstanceId: "agent-1",
                AgentTemplateId: null,
                SessionId: "session-1",
                ConversationText: "   ",
                Reason: "manual",
                MemoryLlmConfig: null));

        Assert.AreEqual(string.Empty, result);
        Assert.AreEqual(0, llm.CallCount);
    }

    [TestMethod]
    public async Task CompressConversationAsync_UsesStructuredCompactionPrompt()
    {
        var llm = new RecordingMemoryLlmClient("<compact_summary>done</compact_summary>");
        var service = new SubconsciousTextProcessingService(llm);

        var result = await service.CompressConversationAsync(
            new ConversationCompressionRequest(
                WorkspaceId: "workspace-1",
                AgentInstanceId: "agent-1",
                AgentTemplateId: "template-1",
                SessionId: "session-1",
                Messages: new[]
                {
                    new ConversationCompressionMessage("user", 1, "需要实现每日摘要"),
                    new ConversationCompressionMessage("agent", 2, "已完成设计"),
                },
                Reason: "auto",
                MemoryLlmConfig: null));

        Assert.AreEqual("<compact_summary>done</compact_summary>", result);
        StringAssert.Contains(llm.LastSystemPrompt!, "<compact_summary>");
        StringAssert.Contains(llm.LastUserMessage!, "[user #1]");
        StringAssert.Contains(llm.LastUserMessage!, "需要实现每日摘要");
    }

    private sealed class RecordingMemoryLlmClient(string response) : IMemoryLlmClient
    {
        public int CallCount { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserMessage { get; private set; }
        public MemoryLlmConfig? LastConfig { get; private set; }

        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> ChatWithConfigAsync(
            string systemPrompt,
            string userMessage,
            MemoryLlmConfig? memoryLlmConfig,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastSystemPrompt = systemPrompt;
            LastUserMessage = userMessage;
            LastConfig = memoryLlmConfig;
            return Task.FromResult(response);
        }
    }
}
