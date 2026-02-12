using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class MemoryWikiPageUpdateServiceTests
{
    [TestMethod]
    public void ValidateJson_ShouldAcceptMinimalPageUpdate()
    {
        var result = MemoryWikiPageUpdateService.ValidateJson("""
            {
              "schema": "pudding.memory_wiki_page_update.v1",
              "updates": [
                {
                  "book": "记忆系统设计",
                  "page": "/Memory v2/V1 原则",
                  "content": "# V1 原则\n\n- 默认不做。"
                }
              ]
            }
            """);

        Assert.IsTrue(result.IsValid, string.Join(",", result.Errors));
        Assert.IsNotNull(result.Plan);
        Assert.AreEqual("记忆系统设计", result.Plan!.Updates[0].Book);
        Assert.AreEqual("/Memory v2/V1 原则", result.Plan.Updates[0].Page);
    }

    [TestMethod]
    public void ValidateJson_ShouldRejectMissingContent()
    {
        var result = MemoryWikiPageUpdateService.ValidateJson("""
            {
              "schema": "pudding.memory_wiki_page_update.v1",
              "updates": [
                {
                  "book": "记忆系统设计",
                  "page": "/Memory v2/V1 原则",
                  "content": ""
                }
              ]
            }
            """);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.Contains(result.Errors.ToArray(), "missing_content");
    }

    [TestMethod]
    public async Task GenerateAsync_ShouldUseMemoryNotesAsPrimaryInput()
    {
        var llm = new RecordingMemoryLlmClient("""
            {
              "schema": "pudding.memory_wiki_page_update.v1",
              "updates": [
                {
                  "book": "用户偏好",
                  "page": "/设计",
                  "content": "# 设计\n\n- 用户偏好简单 V1。"
                }
              ]
            }
            """);
        var service = new MemoryWikiPageUpdateService(llm);

        var result = await service.GenerateAsync(new MemoryWikiPageUpdateRequest
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentId = "agent-1",
            AgentTemplateId = "template-1",
            MemoryScope = new SubconsciousMemoryScope
            {
                WorkspaceId = "workspace-1",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
                SessionId = "session-1",
            },
            MemoryNotes = ["用户偏好简单 V1。"],
        });

        Assert.IsTrue(result.IsValid, string.Join(",", result.Errors));
        Assert.AreEqual(1, llm.CallCount);
        StringAssert.Contains(llm.LastUserMessage!, "memoryNotes");
        StringAssert.Contains(llm.LastUserMessage!, "用户偏好简单 V1。");
        StringAssert.Contains(llm.LastSystemPrompt!, "Do not emit reuse, append, supersede, merge");
    }

    private sealed class RecordingMemoryLlmClient(string response) : IMemoryLlmClient
    {
        public int CallCount { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserMessage { get; private set; }

        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> ChatWithScopedConfigAsync(
            string systemPrompt,
            string userMessage,
            MemoryLlmConfig? memoryLlmConfig,
            SubconsciousMemoryScope targetScope,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastSystemPrompt = systemPrompt;
            LastUserMessage = userMessage;
            return Task.FromResult(response);
        }
    }
}
