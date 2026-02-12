using System.Text;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// Text processing facade for daily summaries, rolling summaries, and conversation compaction.
/// </summary>
public sealed class SubconsciousTextProcessingService(IMemoryLlmClient memoryLlmClient)
    : ISubconsciousTextProcessingService
{
    public async Task<string> SummarizeDailyLogAsync(
        DailyLogSummaryRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.OrdinaryLogMarkdown))
            return string.Empty;

        const string systemPrompt = """
你是 Pudding 的潜意识文本处理服务。请把某个 Agent 一天的普通消息日志总结成精简的 Markdown 索引。
要求：
- 只记录重要工作、关键决策、涉及文件、问题、后续事项和用户明确偏好。
- 保持精简、短小，适合作为后续召回索引。
- 不记录闲聊、重复状态、无价值输出。
- 不输出思维链，不扩写，不编造。
- 输出 Markdown，不要包裹代码块。
""";

        var userMessage = $"""
workspaceId: {request.WorkspaceId}
agentInstanceId: {request.AgentInstanceId}
agentTemplateId: {request.AgentTemplateId ?? ""}
day: {request.Day}

普通消息日志：
{request.OrdinaryLogMarkdown.Trim()}
""";

        return Normalize(await memoryLlmClient.ChatWithConfigAsync(
            systemPrompt,
            userMessage,
            request.MemoryLlmConfig,
            tools: null,
            ct));
    }

    public async Task<string> SummarizeCurrentSessionAsync(
        CurrentSessionSummaryRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationText))
            return string.Empty;

        const string systemPrompt = """
你是 Pudding 的潜意识文本处理服务。请把当前未结束会话总结为当天 content.md 的滚动摘要。
要求：
- 只保留对继续工作有用的目标、进展、决策、文件、问题、下一步和约束。
- 保持精简，作为新 session 冷启动上下文。
- 不输出思维链，不记录无关闲聊，不编造。
- 输出 Markdown，不要包裹代码块。
""";

        var userMessage = $"""
workspaceId: {request.WorkspaceId}
agentInstanceId: {request.AgentInstanceId}
agentTemplateId: {request.AgentTemplateId ?? ""}
sessionId: {request.SessionId}
reason: {request.Reason}

当前会话内容：
{request.ConversationText.Trim()}
""";

        return Normalize(await memoryLlmClient.ChatWithConfigAsync(
            systemPrompt,
            userMessage,
            request.MemoryLlmConfig,
            tools: null,
            ct));
    }

    public async Task<string> CompressConversationAsync(
        ConversationCompressionRequest request,
        CancellationToken ct = default)
    {
        var messages = request.Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .OrderBy(m => m.Sequence)
            .ToList();

        if (messages.Count == 0)
            return string.Empty;

        const string systemPrompt = """
你是 Pudding 的会话压缩服务。请把旧会话消息压缩成结构化摘要，供 Agent 在后续上下文中继续工作。
必须输出以下 Markdown 结构，并保留 <compact_summary> 根标签：
<compact_summary>
## 用户目标
## 已完成事项
## 关键决策
## 涉及文件和代码位置
## 工具调用与重要输出
## 错误、阻塞与修复
## 当前工作状态
## 明确的下一步
## 保留的用户偏好和约束
</compact_summary>

要求：精简、事实化、不输出思维链、不编造。
""";

        var sb = new StringBuilder();
        sb.AppendLine($"workspaceId: {request.WorkspaceId}");
        sb.AppendLine($"agentInstanceId: {request.AgentInstanceId}");
        sb.AppendLine($"agentTemplateId: {request.AgentTemplateId ?? ""}");
        sb.AppendLine($"sessionId: {request.SessionId}");
        sb.AppendLine($"reason: {request.Reason}");
        sb.AppendLine();
        sb.AppendLine("待压缩消息：");
        foreach (var message in messages)
        {
            sb.AppendLine($"[{message.Role} #{message.Sequence}]");
            sb.AppendLine(message.Content.Trim());
            sb.AppendLine();
        }

        return Normalize(await memoryLlmClient.ChatWithConfigAsync(
            systemPrompt,
            sb.ToString(),
            request.MemoryLlmConfig,
            tools: null,
            ct));
    }

    private static string Normalize(string value) => value.Trim();
}
