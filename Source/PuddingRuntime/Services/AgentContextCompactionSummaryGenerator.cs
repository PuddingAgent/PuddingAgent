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

    public AgentContextCompactionSummaryGenerator(
        IServiceProvider services,
        ContextCompactionOptions options,
        ILogger<AgentContextCompactionSummaryGenerator> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    public async Task<string> GenerateSummaryAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct = default)
    {
        var dispatcher = _services.GetRequiredService<IRuntimeAgentDispatcher>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.AgentSummaryTimeoutSeconds)));

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
        sb.AppendLine("只保留事实、用户目标、关键决策、涉及文件、工具结果、错误修复、当前状态和下一步。");
        sb.AppendLine("必须包含一节 `## Memory Notes`，用短 bullet 列出本轮窗口内应该进入长期记忆系统的信息；没有则写 `- 无`。");
        sb.AppendLine("不要输出寒暄或标签外内容。");
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
