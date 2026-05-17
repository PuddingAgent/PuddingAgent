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
///   · agent.sub_completed — 异步子代理完成 → 注入父代理上下文 ⭐
///   · message.* — 用户消息（Chat/CLI 等通道，未来由消息系统发送）
/// </summary>
public class AgentEventHandler : IEventHandler
{
    private readonly AgentExecutionService _executionService;
    private readonly ContextWindowManager _contextManager;
    private readonly IAgentCheckpointService _checkpointService;
    private readonly ILogger<AgentEventHandler> _logger;
    private readonly IServiceProvider _services;

    public string EventTypePattern => "*";

    public bool SupportsInterruption => true;

    public AgentEventHandler(
        AgentExecutionService executionService,
        ContextWindowManager contextManager,
        IAgentCheckpointService checkpointService,
        ILogger<AgentEventHandler> logger,
        IServiceProvider services)
    {
        _executionService = executionService;
        _contextManager = contextManager;
        _checkpointService = checkpointService;
        _logger = logger;
        _services = services;
    }

    public async Task<bool> HandleAsync(InternalEvent evt, CancellationToken ct)
    {
        _logger.LogInformation(
            "[AgentEventHandler] Handling event id={Id} type={Type} pri={Priority} isolation={Isolation}",
            evt.EventId, evt.Type, evt.Priority, evt.Isolation);

        try
        {
            // ── agent.sub_completed：异步子代理结果注入父代理上下文 ──
            if (evt.Type == "agent.sub_completed")
            {
                _logger.LogDebug("[Diag] Event handler sub_completed event={EventId} parentSession={Parent} payload={Payload}",
                    evt.EventId, evt.SessionId, evt.Payload);
                return await HandleSubCompletedAsync(evt);
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

    /// <summary>
    /// 处理异步子代理完成事件：将子代理结果注入父代理会话上下文，并触发执行引擎处理。
    /// 
    /// 采用 Claude Code 的 XML 通知模式：
    ///   父代理上下文中追加 user-role 消息，包含子代理 ID、状态、结果。
    ///   然后调用 ExecuteAsync 触发父代理处理此通知（就像用户发了新消息一样）。
    /// 
    /// 注意：事件系统是 BackgroundService 驱动的，不绑定 HTTP 请求生命周期。
    /// </summary>
    private async Task<bool> HandleSubCompletedAsync(InternalEvent evt)
    {
        var parentSessionId = evt.SessionId;
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            _logger.LogWarning("[AgentEventHandler] Sub completed without parent session id={Id}", evt.EventId);
            return true;
        }

        // 提取子代理信息
        string subAgentId = "unknown";
        string? reply = null;
        bool success = false;
        string? error = null;

        if (evt.Payload is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("sub_agent_id", out var idEl)) subAgentId = idEl.GetString() ?? subAgentId;
            if (je.TryGetProperty("success", out var okEl)) success = okEl.GetBoolean();
            if (je.TryGetProperty("reply", out var repEl)) reply = repEl.GetString();
            if (je.TryGetProperty("error", out var errEl)) error = errEl.GetString();
        }

        // 构造注入消息 — Claude Code XML notification 模式
        var sb = new StringBuilder();
        sb.AppendLine("<task-notification>");
        sb.AppendLine($"  <task-id>{subAgentId}</task-id>");
        sb.AppendLine($"  <status>{(success ? "completed" : "failed")}</status>");
        if (success)
        {
            var summary = reply?.Length > 200 ? reply[..200] + "..." : reply ?? "(empty)";
            sb.AppendLine($"  <summary>{summary}</summary>");
            sb.AppendLine($"  <result>{reply ?? "(empty)"}</result>");
        }
        else
        {
            sb.AppendLine($"  <error>{error ?? "unknown error"}</error>");
        }
        sb.AppendLine("</task-notification>");

        var notificationText = sb.ToString();

        // 注入父代理上下文
        var history = _contextManager.GetOrCreateHistory(parentSessionId);
        history.Add(new ChatMessage(ChatRole.User, notificationText));

        // Push SubAgentCompleted 帧到 SessionStateManager → Channel → 前端 SSE
        _ = PushSubAgentCompletedAsync(parentSessionId, subAgentId, success, reply, error);

        _logger.LogInformation(
            "[AgentEventHandler] Sub-agent result injected parent={Parent} sub={Sub} success={Success}, triggering execution",
            parentSessionId, subAgentId, success);

        // ── 触发父代理执行引擎处理通知，流式推送帧到前端 ──
        try
        {
            var request = new RuntimeDispatchRequest
            {
                SessionId = parentSessionId,
                WorkspaceId = evt.WorkspaceId ?? "default",
                AgentTemplateId = "general-assistant",
                MessageText = notificationText,
                MaxRounds = 3,
            };

            _logger.LogInformation("[AgentEventHandler] Starting stream notification session={Session} sub={Sub} textLen={Len}",
                parentSessionId, subAgentId, notificationText.Length);

            // 使用流式执行，逐帧写入 SessionStateManager → Channel → 前端 SSE
            int frameCount = 0;
            string lastEvent = "";
            var ssmLocal = _services.GetService<ISessionStateManager>();
            if (ssmLocal is null)
            {
                _logger.LogWarning("[AgentEventHandler] ISessionStateManager not available for streaming");
            }

            await foreach (var frame in _executionService.ExecuteStreamAsync(request, CancellationToken.None))
            {
                frameCount++;
                lastEvent = frame.Event;
                if (ssmLocal is not null)
                {
                    try
                    {
                        await ssmLocal.AppendAsync(parentSessionId, evt.WorkspaceId ?? "default", frame, CancellationToken.None);
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogWarning(innerEx, "[AgentEventHandler] SSM AppendAsync failed session={Session} frame={Count} type={Type}",
                            parentSessionId, frameCount, frame.Event);
                    }
                }
                if (frameCount <= 3 || frame.Event == "done" || frame.Event == "error")
                {
                    _logger.LogDebug("[AgentEventHandler] Stream frame {Count}/{Type} session={Session}",
                        frameCount, frame.Event, parentSessionId);
                }
            }

            _logger.LogInformation(
                "[AgentEventHandler] Sub notification stream done parent={Parent} sub={Sub} frames={Frames} lastEvent={LastEvent}",
                parentSessionId, subAgentId, frameCount, lastEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[AgentEventHandler] Sub notification stream exception parent={Parent} sub={Sub} error={Error}",
                parentSessionId, subAgentId, ex.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 通过 ISessionStateManager.AppendAsync 推送 SubAgentCompleted 帧。
    /// 替换旧的反射调用 SessionEventHub.TryPushToSessionHub。
    /// </summary>
    private async Task PushSubAgentCompletedAsync(
        string sessionId, string subAgentId, bool success, string? reply, string? error)
    {
        try
        {
            var ssm = _services.GetService<ISessionStateManager>();
            if (ssm is null) return;

            var frame = ServerSentEventFrame.Json(SessionEventTypes.SubAgentCompleted, new
            {
                sub_agent_id = subAgentId,
                success,
                reply,
                error,
            });
            await ssm.AppendAsync(sessionId, "", frame, CancellationToken.None);

            _logger.LogInformation(
                "[AgentEventHandler] SubAgentCompleted frame pushed session={Session} sub={Sub}",
                sessionId, subAgentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[AgentEventHandler] PushSubAgentCompleted failed session={Session}", sessionId);
        }
    }

    /// <summary>
    /// [DEPRECATED] 旧的 SessionEventHub 反射推送 — 由 PushSubAgentCompletedAsync 替代。
    /// 保留以兼容现有 SessionEventHub 消费者。
    /// </summary>
    private void TryPushToSessionHub(string sessionId, string subAgentId, bool success, string? reply, string? error)
    {
        try
        {
            var hubType = Type.GetType("PuddingPlatform.Services.SessionEventHub, PuddingPlatform");
            if (hubType is null) return;
            var hub = _services.GetService(hubType);
            if (hub is null) return;
            var channel = hubType.GetMethod("GetOrCreate")?.Invoke(hub, [sessionId]);
            if (channel is null) return;

            var writer = channel.GetType().GetProperty("Writer")?.GetValue(channel);
            if (writer is null) return;

            var notification = new ServerSentEventFrame("subagent.done",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    sub_agent_id = subAgentId,
                    status = success ? "completed" : "failed",
                    summary = reply?.Length > 200 ? reply[..200] + "..." : reply ?? "(empty)",
                    reply,
                    error,
                }));

            var tryWrite = writer.GetType().GetMethod("TryWrite", [typeof(ServerSentEventFrame)]);
            tryWrite?.Invoke(writer, [notification]);

            _logger.LogInformation(
                "[AgentEventHandler] Pushed sub-agent notification to SessionEventHub session={Session}",
                sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "[AgentEventHandler] Failed to push notification to SessionEventHub (non-critical)");
        }
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────

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
        };
    }
}
