using System.Text;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 压缩前冲洗服务（Pre-Compaction Flush）。
/// 借鉴 Claude Code 的 pre-compaction flush 模式：
/// 在上下文压缩前，用 Flash LLM 快速提取关键事实，
/// 防止压缩导致用户偏好、项目决策等重要信息丢失。
///
/// 流程：
/// 1. ContextWindowManager 触发压缩 → 调用 FlushAsync
/// 2. Flash LLM 提取关键事实（用户偏好、项目决策、教训）
/// 3. 事实写入会话历史（标记为 [PreCompactFlush]）
/// 4. 后续由潜意识 LLM 将这些事实转化为正式记忆
///
/// 设计原则：
/// - 只用 Flash 模型（低成本、低延迟）
/// - 异步不阻塞压缩（失败只记日志）
/// - 可观测（日志带 [PreCompactFlush] 前缀）
/// </summary>
public sealed class PreCompactionFlushService : IPreCompactionFlushService
{
    private readonly IMemoryLlmClient _memoryLlmClient;
    private readonly ILLMConfigResolver _llmConfigResolver;
    private readonly ILogger<PreCompactionFlushService> _logger;

    private const string FlushSystemPrompt = """
你是 Pudding 的压缩前冲洗服务。你的任务是从会话消息中提取值得长期保留的关键事实。
只提取以下类型的信息：

1. **用户偏好** — 用户表达的偏好、约束、沟通风格、工具偏好
2. **项目事实** — 项目名称、技术栈、目录结构、关键配置
3. **决策记录** — 重要的技术决策及原因
4. **经验教训** — 已验证的方法、要避免的陷阱、重复出现的模式

不要提取：
- 能用 git 找到的东西
- 进行中的任务状态
- 一次性操作细节
- 调试过程

输出格式：每行一条事实，以 "- " 开头。最多输出 8 条。
对不重要的会话输出 "无关键事实"。
不要输出任何其他内容。
""";

    public PreCompactionFlushService(
        IMemoryLlmClient memoryLlmClient,
        ILLMConfigResolver llmConfigResolver,
        ILogger<PreCompactionFlushService> logger)
    {
        _memoryLlmClient = memoryLlmClient;
        _llmConfigResolver = llmConfigResolver;
        _logger = logger;
    }

    public async Task<PreCompactionFlushResult> FlushAsync(
        PreCompactionFlushRequest request,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 1. 过滤并构建用户消息
            var messages = request.Messages
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .ToList();

            if (messages.Count == 0)
            {
                _logger.LogInformation(
                    "[PreCompactFlush] Skip: no messages session={SessionId}",
                    request.SessionId);
                return new PreCompactionFlushResult(0, sw.ElapsedMilliseconds);
            }

            var userMessage = BuildUserMessage(messages, request);

            // 2. 解析 Flash 模型
            var templateId = request.AgentTemplateId ?? request.AgentId ?? "default-agent";
            var memoryConfig = await _llmConfigResolver.ResolveMemoryAsync(
                templateId, request.WorkspaceId, ct);
            var flashModelId = memoryConfig?.ModelId ?? "deepseek-v4-flash";

            _logger.LogInformation(
                "[PreCompactFlush] Calling Flash LLM session={SessionId} model={ModelId} msgCount={Count}",
                request.SessionId, flashModelId, messages.Count);

            var flashConfig = new MemoryLlmConfig(
                Endpoint: null,
                ApiKey: null,
                ModelId: flashModelId);

            // 3. 调用 Flash LLM
            string result;
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
            {
                try
                {
                    result = await _memoryLlmClient.ChatWithConfigAsync(
                        FlushSystemPrompt,
                        userMessage,
                        flashConfig,
                        tools: null,
                        linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "[PreCompactFlush] Timeout after 8s session={SessionId}",
                        request.SessionId);
                    return new PreCompactionFlushResult(0, sw.ElapsedMilliseconds);
                }
            }

            // 4. 解析结果
            var facts = ParseFacts(result);
            sw.Stop();

            var flushContent = facts.Count > 0 ? string.Join("\n", facts) : null;

            _logger.LogInformation(
                "[PreCompactFlush] Completed session={SessionId} extracted={Count} duration={DurationMs}ms",
                request.SessionId, facts.Count, sw.ElapsedMilliseconds);

            if (facts.Count > 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "[PreCompactFlush] Facts session={SessionId} content={Content}",
                    request.SessionId, flushContent);
            }

            return new PreCompactionFlushResult(facts.Count, sw.ElapsedMilliseconds, flushContent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(
                ex,
                "[PreCompactFlush] Failed session={SessionId}: {Message}",
                request.SessionId, ex.Message);
            return new PreCompactionFlushResult(0, sw.ElapsedMilliseconds);
        }
    }

    private static string BuildUserMessage(
        IReadOnlyList<ContextCompactionMessage> messages,
        PreCompactionFlushRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"sessionId: {request.SessionId}");
        sb.AppendLine($"reason: {request.Reason}");

        if (!string.IsNullOrWhiteSpace(request.AgentWorkSummary))
        {
            sb.AppendLine();
            sb.AppendLine("═══ Agent 工作总结（优先参考）═══");
            sb.AppendLine(request.AgentWorkSummary);
            sb.AppendLine("═══ 以上是 Agent 的自我总结 ═══");
        }

        sb.AppendLine();
        sb.AppendLine("会话消息：");
        foreach (var message in messages)
        {
            var content = message.Content.Trim();
            if (content.Length > 2000)
                content = content[..2000] + "\n…[截断]";
            sb.AppendLine($"[{message.Role}]");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<string> ParseFacts(string? llmResponse)
    {
        if (string.IsNullOrWhiteSpace(llmResponse))
            return [];

        var facts = new List<string>();
        foreach (var line in llmResponse.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("-"))
            {
                var fact = trimmed.StartsWith("- ") ? trimmed[2..] : trimmed[1..];
                fact = fact.Trim();
                if (!string.IsNullOrWhiteSpace(fact)
                    && !fact.Contains("无关键事实", StringComparison.OrdinalIgnoreCase))
                {
                    facts.Add(fact);
                }
            }
        }

        return facts;
    }
}
