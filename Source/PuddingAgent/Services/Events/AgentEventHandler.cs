using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingAgent.Services.Events;

/// <summary>
/// AgentEventHandler — 事件系统到 Agent 执行引擎的桥接层。
/// 
/// 实现 IEventHandler，将 InternalEvent 转换为 RuntimeDispatchRequest，
/// 调用 AgentExecutionService.ExecuteAsync 执行。
/// 
/// 不是事件系统的一部分 — 它是事件系统的消费者。
/// 事件系统只知道 IEventHandler 接口，不知道 AgentExecutionService 的存在。
/// 
/// 支持的隐式订阅场景：
///   · cron.trigger — 定时任务触发
///   · connector.* — 连接器入站事件
///   · p2p.message — Agent 间消息
///   · agent.wakeup — WAIT 态唤醒
///   · message.* — 用户消息（Chat/CLI 等通道消息，未来由消息系统发送）
/// </summary>
public class AgentEventHandler : IEventHandler
{
    private readonly AgentExecutionService _executionService;
    private readonly IAgentCheckpointService _checkpointService;
    private readonly ILogger<AgentEventHandler> _logger;

    /// <summary>
    /// 匹配所有需要在 Agent Loop 中执行的事件类型。
    /// 未来 message.* 由消息系统调用，当前保留 cron/connector/p2p/agent。
    /// </summary>
    public string EventTypePattern => "*";

    /// <summary>Agent 执行可以被更高优先级事件打断。</summary>
    public bool SupportsInterruption => true;

    public AgentEventHandler(
        AgentExecutionService executionService,
        IAgentCheckpointService checkpointService,
        ILogger<AgentEventHandler> logger)
    {
        _executionService = executionService;
        _checkpointService = checkpointService;
        _logger = logger;
    }

    public async Task<bool> HandleAsync(InternalEvent evt, CancellationToken ct)
    {
        _logger.LogInformation(
            "[AgentEventHandler] Handling event id={Id} type={Type} pri={Priority} isolation={Isolation}",
            evt.EventId, evt.Type, evt.Priority, evt.Isolation);

        try
        {
            // 将事件负载转换为执行请求
            var request = BuildRequest(evt);

            if (request == null)
            {
                _logger.LogWarning(
                    "[AgentEventHandler] Cannot build request for event id={Id} type={Type}",
                    evt.EventId, evt.Type);
                return true; // 不可执行的事件不影响重试 —— 沉没
            }

            // 使用 CancellationToken.None — 事件驱动执行不绑定 HTTP 请求生命周期
            var result = await _executionService.ExecuteAsync(request, CancellationToken.None);

            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "[AgentEventHandler] Event completed id={Id} type={Type} session={Session}",
                    evt.EventId, evt.Type, result.SessionId);
                return true;
            }

            _logger.LogWarning(
                "[AgentEventHandler] Event failed id={Id} type={Type} error={Error}",
                evt.EventId, evt.Type, result.ErrorMessage);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[AgentEventHandler] Event cancelled id={Id} type={Type}", evt.EventId, evt.Type);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[AgentEventHandler] Event exception id={Id} type={Type}", evt.EventId, evt.Type);
            return false;
        }
    }

    /// <summary>
    /// 将 InternalEvent 转换为 RuntimeDispatchRequest。
    /// 事件负载的 Prompt 字段映射为 MessageText。
    /// 如果事件没有 Prompt，从 Type 生成系统消息。
    /// </summary>
    private static RuntimeDispatchRequest? BuildRequest(InternalEvent evt)
    {
        var sessionId = evt.SessionId ?? $"evt-{evt.EventId[..Math.Min(evt.EventId.Length, 12)]}";
        var workspaceId = evt.WorkspaceId ?? "default";

        // 从 Payload 提取 prompt
        string messageText;
        if (evt.Payload is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("Prompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.String)
                messageText = promptEl.GetString()!;
            else if (je.TryGetProperty("prompt", out var promptEl2) && promptEl2.ValueKind == JsonValueKind.String)
                messageText = promptEl2.GetString()!;
            else if (je.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                messageText = msgEl.GetString()!;
            else
                messageText = $"[系统事件] {evt.Type}";
        }
        else if (evt.Payload is string s && !string.IsNullOrWhiteSpace(s))
        {
            messageText = s;
        }
        else
        {
            messageText = $"[系统事件] {evt.Type}";
        }

        return new RuntimeDispatchRequest
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            AgentTemplateId = evt.AgentId ?? "service-agent",
            MessageText = messageText,
        };
    }
}
