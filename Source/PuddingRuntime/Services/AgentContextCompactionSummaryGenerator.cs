using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// Asks the current runtime agent to produce the compaction summary, preserving
/// the agent's task semantics instead of relying only on extractive snippets.
/// </summary>
public sealed class AgentContextCompactionSummaryGenerator : IContextCompactionSummaryGenerator
{
    private readonly IServiceProvider _services;
    private readonly ContextCompactionOptions _options;
    private readonly ILogger<AgentContextCompactionSummaryGenerator> _logger;
    private readonly ILlmResolver _llmResolver;

    public AgentContextCompactionSummaryGenerator(
        IServiceProvider services,
        ContextCompactionOptions options,
        ILogger<AgentContextCompactionSummaryGenerator> logger,
        ILlmResolver llmResolver)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _llmResolver = llmResolver;
    }

    public async Task<string> GenerateSummaryAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct = default)
    {
        var dispatcher = _services.GetRequiredService<IRuntimeAgentDispatcher>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.AgentSummaryTimeoutSeconds)));

        // 压缩是一次独立的 LLM 调用，必须在派发前解出 provider/model；
        // 否则下游 RequireInvocationProfile 只会报「missing LlmProfile」，无法定位。
        var llmRoute = string.IsNullOrWhiteSpace(request.LlmConfig?.ModelId)
            ? null
            : await _llmResolver.ResolveRouteAsync(request.LlmConfig!.ModelId, requiredCapabilityTags: null, ct);

        var dispatchRequest = new RuntimeDispatchRequest
        {
            SessionId = request.SessionId,
            WorkspaceId = request.WorkspaceId,
            AgentTemplateId = request.AgentTemplateId ?? request.AgentId ?? "default-agent",
            AgentInstanceId = request.AgentId,
            UserId = request.UserId,
            MessageText = BuildPrompt(request),
            MaxRounds = 1,
            SuppressContextAutoCompaction = true,
            AssignedObjective = "Generate semantic context compaction summary.",
            ExpectedOutputContract = "Return only the <compact_summary> markdown block.",
            LlmConfig = request.LlmConfig,
            LlmProfile = llmRoute is null
                ? null
                : new LlmInvocationProfile
                {
                    ProviderId = llmRoute.ProviderId,
                    ModelId = llmRoute.ModelId,
                    ProfileId = $"agent:{request.AgentId ?? "unknown"}:compaction",
                    Role = "compaction",
                },
            CapabilityPolicy = request.CapabilityPolicy,
            ToolDefinitions = request.ToolDefinitions,
            SkillPackages = request.SkillPackages,
        };

        try
        {
            await foreach (var frame in dispatcher.DispatchStreamAsync(dispatchRequest, timeoutCts.Token))
            {
                if (string.Equals(frame.Event, SseEventTypes.Error, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(frame.Event, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var message = TryReadStringProperty(frame.Data, "message")
                                  ?? TryReadStringProperty(frame.Data, "error")
                                  ?? frame.Data;
                    throw new InvalidOperationException(
                        $"Agent context compaction summary failed: {message}");
                }

                if (!string.Equals(frame.Event, SseEventTypes.Done, StringComparison.OrdinalIgnoreCase))
                    continue;

                var reply = TryReadStringProperty(frame.Data, "reply");
                if (!string.IsNullOrWhiteSpace(reply))
                    return reply.Trim();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Agent context compaction summary timed out after {_options.AgentSummaryTimeoutSeconds} seconds.");
        }

        _logger.LogWarning(
            "[AgentCompactionSummary] Agent returned no summary session={SessionId}",
            request.SessionId);
        return string.Empty;
    }

    private static string BuildPrompt(ContextCompactionSummaryRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("请把以下即将压缩的旧会话消息总结为 <compact_summary>...</compact_summary>。");
        sb.AppendLine("严格按下面 HandoffGuidance 给出的**小节结构**与**长度预算（2500–4000 字符）**输出。");
        sb.AppendLine("区分用户明确要求、助手建议与工具报告；用户后续纠正优先。区分已完成操作和待办，不把历史动作变成新任务。未知内容明确标注，不推断授权。来源定位由宿主添加，不编造 Turn ID，也不复制旧的 <compaction-evidence> 块。");
        sb.AppendLine("**可核查事实（数字、坐标、句柄、路径、命令、错误码、版本号）必须原样保留**，这是下一轮回查的锚点。");
        sb.AppendLine("**失败尝试与被实测推翻的假设必须保留**，写清“假设 → 实验 → 实测 → 结论”，不要省略。");
        sb.AppendLine("必须包含一节 `## Memory Notes`，用短 bullet 列出本轮窗口内应该进入长期记忆系统的信息；没有则写 `- 无`。");
        sb.AppendLine("不要输出寒暄或标签外内容。");
        sb.AppendLine(PuddingContextHandoff.HandoffGuidance.ForCompaction);
        sb.AppendLine();
        sb.AppendLine($"workspaceId: {request.WorkspaceId}");
        sb.AppendLine($"sessionId: {request.SessionId}");
        if (!string.IsNullOrWhiteSpace(request.AgentId))
            sb.AppendLine($"agentId: {request.AgentId}");
        sb.AppendLine($"reason: {request.Reason}");

        if (!string.IsNullOrWhiteSpace(request.AgentWorkSummary))
        {
            sb.AppendLine();
            sb.AppendLine("Agent 主动工作总结：");
            sb.AppendLine(request.AgentWorkSummary);
        }

        sb.AppendLine();
        sb.AppendLine("待压缩消息：");
        foreach (var message in request.Messages.OrderBy(m => m.Sequence))
        {
            sb.AppendLine($"[{message.Sequence}] {message.Role}:");
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? TryReadStringProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
