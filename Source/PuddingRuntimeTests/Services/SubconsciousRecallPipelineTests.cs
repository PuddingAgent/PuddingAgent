using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubconsciousRecallPipelineTests
{
    [TestMethod]
    public async Task RunAsync_PassesWorkspaceIdToMemoryRecall()
    {
        var recall = new RecordingMemoryRecallService();
        var llm = new StaticMemoryLlmClient("""{"need_recall":true,"relevant_ids":[1],"reason":"test"}""");
        var pipeline = new SubconsciousRecallPipeline(
            recall,
            llm,
            NullLogger<SubconsciousRecallPipeline>.Instance);

        var result = await pipeline.RunAsync(
            "继续 回顾 上次 讨论",
            workspaceId: "default",
            agentInstanceId: "agent-1",
            isFirstMessage: false,
            CancellationToken.None);

        Assert.AreEqual("default", recall.LastWorkspaceId);
        Assert.AreEqual("agent-1", recall.LastAgentInstanceId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result));
    }

    private sealed class RecordingMemoryRecallService : IMemoryRecallService
    {
        public string? LastWorkspaceId { get; private set; }
        public string? LastAgentInstanceId { get; private set; }

        public Task<MemoryRecallResult> RecallAsync(
            string query,
            string workspaceId,
            string? agentInstanceId = null,
            IReadOnlyList<string>? recentContext = null,
            int topK = 10,
            CancellationToken ct = default)
        {
            LastWorkspaceId = workspaceId;
            LastAgentInstanceId = agentInstanceId;
            return Task.FromResult(new MemoryRecallResult
            {
                Items =
                [
                    new RecalledMemory
                    {
                        Snippet = "用户上次讨论了 compact 后的 Session 切换和消息吞掉问题。",
                        RelevanceScore = 0.95,
                        Source = "fact",
                        SourceId = "fact-1",
                    },
                ],
            });
        }

        public Task<MemoryRecallStatus> GetStatusAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult(new MemoryRecallStatus());
    }

    private sealed class StaticMemoryLlmClient(string response) : IMemoryLlmClient
    {
        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => Task.FromResult(new MemoryClassification(false, 0, 1, null, null));

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult<MemoryQueryIntent?>(null);

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
            => Task.FromResult(response);
    }
}
