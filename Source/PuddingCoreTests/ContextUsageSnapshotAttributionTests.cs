using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// 上下文用量进度条的分层归因：请求组装时按来源把消息 token 拆成互斥的桶，
/// 供前端绘制彩色分段 + 图例（用户 2026-09-19 诉求）。
/// 不变量：五个桶之和恒等于 MessageTokens。
/// </summary>
[TestClass]
public sealed class ContextUsageSnapshotAttributionTests
{
    private const string SummaryMarker = "<compact_summary>";

    [TestMethod]
    public void CaptureLlmRequest_SplitsMessagesIntoMutuallyExclusiveSourceBuckets()
    {
        var store = new ContextUsageSnapshotStore();

        var snapshot = store.CaptureLlmRequest(
            "session-buckets",
            [
                new ChatMessage(ChatRole.System, "你是 PuddingAgent，负责执行任务。"),
                new ChatMessage(ChatRole.User, "帮我检查一下仓库当前状态。"),
                new ChatMessage(
                    ChatRole.Assistant,
                    "先看一下 git status。",
                    ReasoningContent: "先确认工作区是否干净，再决定下一步动作。"),
                new ChatMessage(
                    ChatRole.Tool,
                    "M Source/PuddingCore/Platform/LlmOptions.cs",
                    ToolCallId: "call_1",
                    ToolName: "shell"),
                new ChatMessage(
                    ChatRole.User,
                    $"{SummaryMarker}旧会话的压缩摘要……{SummaryMarker}"),
            ],
            tools: null,
            modelId: "gpt-5");

        var bucketSum = snapshot.SystemPromptTokens
            + snapshot.CompactionSummaryTokens
            + snapshot.ConversationTokens
            + snapshot.ToolResultTokens
            + snapshot.ReasoningTokens;

        Assert.AreEqual(snapshot.MessageTokens, bucketSum);
        Assert.IsGreaterThan(0, snapshot.SystemPromptTokens);
        Assert.IsGreaterThan(0, snapshot.CompactionSummaryTokens);
        Assert.IsGreaterThan(0, snapshot.ConversationTokens);
        Assert.IsGreaterThan(0, snapshot.ToolResultTokens);
        Assert.IsGreaterThan(0, snapshot.ReasoningTokens);
    }

    [TestMethod]
    public void CaptureLlmRequest_CompactionSummaryIsNotCountedAsConversation()
    {
        var store = new ContextUsageSnapshotStore();

        var snapshot = store.CaptureLlmRequest(
            "session-summary",
            [new ChatMessage(ChatRole.User, $"{SummaryMarker}历史摘要{SummaryMarker}")],
            tools: null,
            modelId: "gpt-5");

        // 标记识别不依赖角色：摘要注入路径可能落在 User 上。
        Assert.AreEqual(0, snapshot.ConversationTokens);
        Assert.AreEqual(snapshot.MessageTokens, snapshot.CompactionSummaryTokens);
    }

    [TestMethod]
    public void CaptureLlmRequest_ReasoningIsDeductedFromConversationBucket()
    {
        var store = new ContextUsageSnapshotStore();

        var withoutReasoning = store.CaptureLlmRequest(
            "session-reasoning-off",
            [new ChatMessage(ChatRole.Assistant, "答案")],
            tools: null,
            modelId: "gpt-5");
        var withReasoning = store.CaptureLlmRequest(
            "session-reasoning-on",
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    "答案",
                    ReasoningContent: new string('思', 400)),
            ],
            tools: null,
            modelId: "gpt-5");

        Assert.AreEqual(0, withoutReasoning.ReasoningTokens);
        Assert.IsGreaterThan(0, withReasoning.ReasoningTokens);
        // 思维链已从对话桶扣除，不重复计量。
        Assert.AreEqual(
            withReasoning.MessageTokens - withReasoning.ReasoningTokens,
            withReasoning.ConversationTokens);
    }
}
