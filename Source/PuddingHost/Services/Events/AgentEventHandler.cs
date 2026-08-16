using System.Text;
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
/// 调用 AgentExecutionService.ExecuteStreamAsync 执行。
/// 
/// 不是事件系统的一部分 — 它是事件系统的消费者。
/// 
/// 处理的事件类型：
///   · cron.trigger — 定时任务触发 → 创建新会话执行
///   · connector.* — 连接器入站事件 → 创建新会话执行
///   · p2p.message — Agent 间消息 → 创建新会话执行
///   · agent.wakeup — WAIT 态唤醒 → 恢复打断会话
///   · message.* — 用户消息（Chat/CLI 等通道，未来由消息系统发送）
/// </summary>
public class AgentEventHandler : IEventHandler
{
    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AgentExecutionService _executionService;
    private readonly ContextWindowManager _contextManager;
    private readonly ILogger<AgentEventHandler> _logger;
    private readonly IServiceProvider _services;

    public string EventTypePattern => "*";

    public bool SupportsInterruption => true;

    public AgentEventHandler(
        AgentExecutionService executionService,
        ContextWindowManager contextManager,
        ILogger<AgentEventHandler> logger,
        IServiceProvider services)
    {
        _executionService = executionService;
        _contextManager = contextManager;
        _logger = logger;
        _services = services;
    }

    public async Task<bool> HandleAsync(InternalEvent evt, CancellationToken ct)
    {
        _logger.LogInformation(
            "[AgentEventHandler] Handling event id={Id} type={Type} pri={Priority} isolation={Isolation}",
            evt.EventId, evt.Type, evt.Priority, evt.Isolation);

        // 内部诊断/状态事件不触发 Agent 执行；返回 true 表示已沉没，避免事件队列重试。
        if (evt.Type.StartsWith("subagent.run.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.Type, "agent.availability.changed", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "[AgentEventHandler] Ignoring internal status event id={Id} type={Type}",
                evt.EventId,
                evt.Type);
            return true;
        }

        try
        {
            // ── message.deliver：消息系统投递给 Agent 的消息 ──
            if (evt.Type == "message.deliver")
            {
                _logger.LogDebug(
                    "[AgentEventHandler] message.deliver skipped event={EventId}; handled by MessageDeliveryDispatcher",
                    evt.EventId);
                return true;
            }

            // ── 其他事件：创建新会话执行 ──
            var request = BuildRequest(evt);

            if (request == null)
            {
                _logger.LogWarning(
                    "[AgentEventHandler] Cannot build request for event id={Id} type={Type}",
                    evt.EventId, evt.Type);
                return true; // 不可执行的事件 → 沉没
            }

            var success = await ExecuteMainEventStreamAsync(evt, request, CancellationToken.None);
            if (success)
            {
                _logger.LogInformation(
                    "[AgentEventHandler] Event completed id={Id} type={Type} session={Session}",
                    evt.EventId, evt.Type, request.SessionId);
                return true;
            }

            _logger.LogWarning(
                "[AgentEventHandler] Event failed id={Id} type={Type} session={Session}",
                evt.EventId, evt.Type, request.SessionId);
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
    /// 主事件链路：统一走 ExecuteStreamAsync。
    /// 注意：ExecuteStreamAsync 内部已负责将帧写入 ISessionStateManager，
    /// 此处只消费帧用于判定执行成功/失败，避免双写。
    /// </summary>
    private async Task<bool> ExecuteMainEventStreamAsync(
        InternalEvent evt,
        RuntimeDispatchRequest request,
        CancellationToken ct)
    {
        var frameCount = 0;
        var deltaCount = 0;
        string? lastEvent = null;
        bool seenDone = false;
        bool seenError = false;
        bool seenCancelled = false;

        await foreach (var frame in _executionService.ExecuteStreamAsync(request, ct))
        {
            frameCount++;
            lastEvent = frame.Event;

            if (frame.Event == "delta")
                deltaCount++;

            if (frame.Event == "done")
                seenDone = true;
            else if (frame.Event == "error")
                seenError = true;
            else if (frame.Event == "cancelled")
                seenCancelled = true;

            if (frameCount <= 3 || frame.Event is "done" or "error" or "cancelled")
            {
                _logger.LogDebug(
                    "[AgentEventHandler] Main stream frame eventId={EventId} session={Session} idx={Idx} type={Type}",
                    evt.EventId, request.SessionId, frameCount, frame.Event);
            }
        }

        _logger.LogInformation(
            "[AgentEventHandler] Main stream finished eventId={EventId} session={Session} frames={Frames} deltas={Deltas} last={Last} done={Done} error={Err} cancelled={Cancelled}",
            evt.EventId,
            request.SessionId,
            frameCount,
            deltaCount,
            lastEvent ?? "(none)",
            seenDone,
            seenError,
            seenCancelled);

        return seenDone && !seenError && !seenCancelled;
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────

    private RuntimeDispatchRequest? BuildMessageDeliverRequest(InternalEvent evt)
    {
        var payload = TryReadMessageDeliverPayload(evt);
        if (payload is null)
        {
            _logger.LogWarning(
                "[AgentEventHandler] message.deliver missing payload event={EventId}",
                evt.EventId);
            return null;
        }

        if (!string.Equals(payload.Target.Kind, MessageEndpointKinds.Agent, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "[AgentEventHandler] Ignoring non-agent message delivery event={EventId} target={Kind}:{Id}",
                evt.EventId,
                payload.Target.Kind,
                payload.Target.Id);
            return null;
        }

        return new RuntimeDispatchRequest
        {
            SessionId = evt.SessionId ?? $"msg-{payload.MessageId}",
            WorkspaceId = payload.WorkspaceId,
            AgentTemplateId = evt.AgentId ?? payload.Target.Id,
            MessageText = payload.Content,
            MessageId = payload.MessageId,
            TaskPlanId = GetMetadataValue(payload.Metadata, "task_plan_id", "taskPlanId", "TaskPlanId"),
            TaskNodeId = GetMetadataValue(payload.Metadata, "task_node_id", "taskNodeId", "TaskNodeId"),
            ParentTaskNodeId = GetMetadataValue(payload.Metadata, "parent_task_node_id", "parentTaskNodeId", "ParentTaskNodeId"),
            DelegationDepth = GetMetadataInt(payload.Metadata, "delegation_depth", "delegationDepth", "DelegationDepth"),
            MaxDelegationDepth = GetMetadataInt(payload.Metadata, "max_delegation_depth", "maxDelegationDepth", "MaxDelegationDepth"),
            RoleInPlan = GetMetadataValue(payload.Metadata, "role_in_plan", "roleInPlan", "RoleInPlan"),
            AllowSubDelegation = GetMetadataBool(payload.Metadata, "allow_sub_delegation", "allowSubDelegation", "AllowSubDelegation"),
            AllowAgentCreation = GetMetadataBool(payload.Metadata, "allow_agent_creation", "allowAgentCreation", "AllowAgentCreation"),
            AssignedObjective = GetMetadataValue(payload.Metadata, "assigned_objective", "assignedObjective", "AssignedObjective"),
            ExpectedOutputContract = GetMetadataValue(payload.Metadata, "expected_output_contract", "expectedOutputContract", "ExpectedOutputContract"),
        };
    }

    private static MessageDeliverEventPayload? TryReadMessageDeliverPayload(InternalEvent evt)
    {
        if (evt.Payload is MessageDeliverEventPayload payload)
            return payload;

        if (evt.Payload is JsonElement json && json.ValueKind == JsonValueKind.Object)
            return JsonSerializer.Deserialize<MessageDeliverEventPayload>(
                json.GetRawText(),
                MessageJsonOptions);

        return null;
    }

    private static RuntimeDispatchRequest? BuildRequest(InternalEvent evt)
    {
        var sessionId = evt.SessionId ?? $"evt-{evt.EventId[..Math.Min(evt.EventId.Length, 12)]}";
        var workspaceId = evt.WorkspaceId ?? "default";

        string messageText;
        if (evt.Payload is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("Prompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.String)
                messageText = promptEl.GetString()!;
            else if (je.TryGetProperty("prompt", out var promptEl2) && promptEl2.ValueKind == JsonValueKind.String)
                messageText = promptEl2.GetString()!;
            else if (je.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                messageText = msgEl.GetString()!;
            else if (je.TryGetProperty("messageText", out var msgTextEl) && msgTextEl.ValueKind == JsonValueKind.String)
                messageText = msgTextEl.GetString()!;
            else if (je.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                messageText = contentEl.GetString()!;
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
            Origin = ResolveOriginFromPayload(evt),
            TaskPlanId = GetMetadataValue(evt.Metadata, "task_plan_id", "taskPlanId", "TaskPlanId"),
            TaskNodeId = GetMetadataValue(evt.Metadata, "task_node_id", "taskNodeId", "TaskNodeId"),
            ParentTaskNodeId = GetMetadataValue(evt.Metadata, "parent_task_node_id", "parentTaskNodeId", "ParentTaskNodeId"),
            DelegationDepth = GetMetadataInt(evt.Metadata, "delegation_depth", "delegationDepth", "DelegationDepth"),
            MaxDelegationDepth = GetMetadataInt(evt.Metadata, "max_delegation_depth", "maxDelegationDepth", "MaxDelegationDepth"),
            RoleInPlan = GetMetadataValue(evt.Metadata, "role_in_plan", "roleInPlan", "RoleInPlan"),
            AllowSubDelegation = GetMetadataBool(evt.Metadata, "allow_sub_delegation", "allowSubDelegation", "AllowSubDelegation"),
            AllowAgentCreation = GetMetadataBool(evt.Metadata, "allow_agent_creation", "allowAgentCreation", "AllowAgentCreation"),
            AssignedObjective = GetMetadataValue(evt.Metadata, "assigned_objective", "assignedObjective", "AssignedObjective"),
            ExpectedOutputContract = GetMetadataValue(evt.Metadata, "expected_output_contract", "expectedOutputContract", "ExpectedOutputContract"),
        };
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
    {
        if (metadata is null)
            return null;

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int? GetMetadataInt(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
        => int.TryParse(GetMetadataValue(metadata, keys), out var value) ? value : null;

    private static bool? GetMetadataBool(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
        => bool.TryParse(GetMetadataValue(metadata, keys), out var value) ? value : null;

    /// <summary>
    /// 从事件负载中提取消息发送方身份，用于构建 INBOUND-MESSAGE-CONTEXT 块。
    /// </summary>
    private static MessageOrigin? ResolveOriginFromPayload(InternalEvent evt)
    {
        // message.deliver 类型的负载已包含完整 From 信息
        if (evt.Payload is MessageDeliverEventPayload p)
        {
            return new MessageOrigin
            {
                FromKind = p.From.Kind,
                FromId = p.From.Id,
                FromDisplayName = p.From.DisplayName,
                MessageType = "agent_message",
            };
        }

        // 尝试从 JsonElement 负载中提取 From 信息
        if (evt.Payload is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("from_agent_id", out var fromId) && fromId.ValueKind == JsonValueKind.String)
            {
                return new MessageOrigin
                {
                    FromKind = "agent",
                    FromId = fromId.GetString()!,
                    FromDisplayName = je.TryGetProperty("from_agent_name", out var fromName) && fromName.ValueKind == JsonValueKind.String
                        ? fromName.GetString()
                        : null,
                    MessageType = "agent_message",
                };
            }
        }

        return null;
    }
}