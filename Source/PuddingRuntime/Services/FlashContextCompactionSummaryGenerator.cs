using System.Text;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 使用 Flash LLM 生成上下文压缩结构化摘要。
/// 输出 &lt;compact_summary&gt; 格式，明确保留用户目标、关键决策、未完成任务、工具结果和约束。
/// 失败时抛出异常，由上层组合生成器 fallback 到 Extractive。
/// </summary>
public sealed class FlashContextCompactionSummaryGenerator : IContextCompactionSummaryGenerator
{
        private readonly IMemoryLlmClient _memoryLlmClient;
    private readonly ILLMConfigResolver _llmConfigResolver;
    private readonly ILogger<FlashContextCompactionSummaryGenerator> _logger;
    private readonly ContextCompactionOptions _options;

    public FlashContextCompactionSummaryGenerator(
        IMemoryLlmClient memoryLlmClient,
        ILLMConfigResolver llmConfigResolver,
        ILogger<FlashContextCompactionSummaryGenerator> logger,
        ContextCompactionOptions options)
    {
        _memoryLlmClient = memoryLlmClient;
        _llmConfigResolver = llmConfigResolver;
        _logger = logger;
        _options = options;
    }

    public async Task<string> GenerateSummaryAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct = default)
    {
        var messages = request.Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .OrderBy(m => m.Sequence)
            .ToList();

        if (messages.Count == 0)
        {
            _logger.LogInformation(
                "[FlashCompaction] Skip: empty input session={SessionId}",
                request.SessionId);
            return string.Empty;
        }

        var totalChars = messages.Sum(m => m.Content.Length);

        const string systemPrompt = """
你是 Pudding 的上下文压缩服务。请把旧会话消息压缩成结构化摘要，供 Agent 在后续上下文中继续工作。
必须输出以下 Markdown 结构，并包裹在 <compact_summary> 根标签内：

<compact_summary>
## 用户目标
用户最初和持续的目标是什么。不要推断，只记录明确表达的目标。

## 已完成事项
已完成的任务、操作和阶段性成果。只记录事实，不编造。

## 关键决策
重要技术决策及其原因。简要说明选择理由。

## 涉及文件和代码位置
提及的具体文件路径、代码位置、函数名、类名等。

## 工具调用与重要输出
关键工具调用及其结果摘要。不重复大段输出，仅记录结论。

## 错误、阻塞与修复
遇到的错误、阻塞问题及已采取的修复措施。

## 当前工作状态
截至压缩时刻的工作进度和状态。

## 明确的下一步
用户或 Agent 明确指出的后续步骤，或根据上下文推断的合理下一步。

## 保留的用户偏好和约束
用户表达的偏好、行为约束、代码风格要求等。
</compact_summary>

要求：
- 精简、事实化、不输出思维链、不编造。
- 明确保留用户目标、关键决策、未完成任务、工具结果、约束。
- 避免寒暄、重复原文、无关 UI 文本。
- 控制输出长度在 800-1500 tokens 以内。
- 对信息不足的章节写"无"或"未检测到"。
- 不输出除 <compact_summary> 以外的任何内容。
""";

                var sb = new StringBuilder();
        sb.AppendLine($"workspaceId: {request.WorkspaceId}");
        sb.AppendLine($"sessionId: {request.SessionId}");
        if (!string.IsNullOrWhiteSpace(request.AgentId))
            sb.AppendLine($"agentId: {request.AgentId}");
        sb.AppendLine($"reason: {request.Reason}");
        sb.AppendLine($"压缩消息数: {messages.Count}");
        sb.AppendLine();

        // 如果有 Agent 主动工作总结，将其作为优先参考内容注入
        if (!string.IsNullOrWhiteSpace(request.AgentWorkSummary))
        {
            sb.AppendLine("═══ Agent 主动工作总结（优先参考）═══");
            sb.AppendLine(request.AgentWorkSummary);
            sb.AppendLine("═══ 以上是 Agent 在压缩前的自我总结，请优先保留其中的关键信息 ═══");
            sb.AppendLine();
        }

        sb.AppendLine("待压缩消息：");
        foreach (var message in messages)
        {
            var content = message.Content.Trim();
            // 截断过长的单条消息，避免超出 Flash 上下文
            if (content.Length > 4000)
                content = content[..4000] + "\n…[截断]";
            sb.AppendLine($"[{message.Role} #{message.Sequence}]");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        var userMessage = sb.ToString();

                // 从 Agent 模板的潜意识配置中解析 flash 模型 ID，而非硬编码
        var templateId = request.AgentTemplateId ?? request.AgentId ?? "default-agent";
        var memoryConfig = await _llmConfigResolver.ResolveMemoryAsync(templateId, request.WorkspaceId, ct);
        var flashModelId = memoryConfig?.ModelId;
        if (string.IsNullOrWhiteSpace(flashModelId))
        {
            _logger.LogWarning(
                "[FlashCompaction] No memory model configured for template={TemplateId}, using fallback",
                templateId);
            flashModelId = "deepseek-v4-flash"; // 最后防线：平台级默认 flash 模型
        }

        _logger.LogInformation(
            "[FlashCompaction] Resolved flash model session={SessionId} template={TemplateId} model={ModelId}",
            request.SessionId, templateId, flashModelId);

        var flashConfig = new MemoryLlmConfig(
            Endpoint: null,
            ApiKey: null,
            ModelId: flashModelId);

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.FlashTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            _logger.LogInformation(
                "[FlashCompaction] Calling Flash LLM session={SessionId} msgCount={MsgCount} totalChars={TotalChars}",
                request.SessionId, messages.Count, totalChars);

            var result = await _memoryLlmClient.ChatWithConfigAsync(
                systemPrompt,
                userMessage,
                flashConfig,
                tools: null,
                linkedCts.Token);

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("Flash LLM returned empty summary.");

            var normalized = result.Trim();
            _logger.LogInformation(
                "[FlashCompaction] Completed session={SessionId} summaryLen={Len}",
                request.SessionId, normalized.Length);

            return normalized;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[FlashCompaction] Timeout after {Timeout}s session={SessionId}",
                _options.FlashTimeoutSeconds, request.SessionId);
            throw new TimeoutException(
                $"Flash compaction summary timed out after {_options.FlashTimeoutSeconds}s");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "[FlashCompaction] Cancelled session={SessionId}", request.SessionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[FlashCompaction] Failed session={SessionId}: {Message}",
                request.SessionId, ex.Message);
            throw;
        }
    }
}
