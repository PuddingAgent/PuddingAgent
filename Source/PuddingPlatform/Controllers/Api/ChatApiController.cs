using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingCode.Services;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatform.Controllers.Api;

/// <summary>管理员 Chat 代理 API — 将消息转发至 Controller 服务的 MessageIngress 端点。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/chat")]
public class ChatApiController(
    PlatformDbContext db,
    PlatformApiClient apiClient,
    MinioStorageService minio,
    AgentTemplateFileService templateFileService,
    WorkspaceAgentFileService workspaceAgentFileService,
    ChatTranscriptWriter transcriptWriter,
    ISessionStateManager ssm,
    ChatTelemetryRecorder telemetry,
    IPuddingToolCatalogService toolCatalog,
    IToolPermissionPolicyService toolPermissionPolicy,
    IToolAuthorizationService toolAuthorizationService,
    IContextCompactionService contextCompactionService,
    IRuntimeControlService runtimeControl,
    SessionSteeringService steeringService,
    ILlmConfigService llmConfigService,
    IHostApplicationLifetime appLifetime,
    SessionRedirectStore redirectStore,
    IRuntimeTraceAccessor traceAccessor,
    TokenUsageRecorder tokenUsageRecorder,
    ChatVisualReasoningSessionRunner visualReasoningRunner,
    SessionTitleService sessionTitleService,
    ILogger<ChatApiController> logger,
    ChatCommandAcceptanceService acceptanceService,
    ChatSystemCommandService systemCommands,
    ChatDispatchService dispatch,
    ChatMessageExecutionService messageExecution) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // POST /api/workspaces/{workspaceId}/chat/message
    // T-102: 改为 fire-and-forget — 立即返回 { messageId, sessionId }，不再等待完整执行结果。
    // 所有流式帧通过 SessionEventsController 的持久 SSE 通道（SSM/EventHub）推送给前端。
    [HttpPost("message")]
    public async Task<IActionResult> SendMessage(
        string workspaceId, [FromBody] AdminChatRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 自动 session 重定向：支持 "main" sentinel + compact 后旧→新透明跳转
        var resolvedAgentId = req.AgentId;
        if (!string.IsNullOrWhiteSpace(req.SessionId))
        {
            var redirected = redirectStore.Resolve(req.SessionId, workspaceId, resolvedAgentId);
            if (!string.Equals(redirected, req.SessionId, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "[Chat] Session redirect ws={Ws} agent={Agent} old={OldSession} → new={NewSession}",
                    workspaceId, resolvedAgentId, req.SessionId, redirected);
                req = req with { SessionId = redirected };
            }
            // "main" sentinel 未被 resolved 为真实 session → 清空让后续 EnsureMainSessionAsync 处理
            else if (string.Equals(req.SessionId, "main", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "[Chat] Session main sentinel unresolved ws={Ws} agent={Agent} — clearing to let EnsureMainSession resolve",
                    workspaceId, resolvedAgentId);
                req = req with { SessionId = null };
            }
        }

        var trace = RuntimeTraceContext.CreateNew(
            sessionId: req.SessionId,
            workspaceId: workspaceId,
            userId: ResolveCurrentUserId());
        traceAccessor.Current = trace;

        logger.LogInformation(
            "[Chat] REQUEST trace={TraceId} ws={WorkspaceId} agentId={AgentId} audience={Audience} targets={TargetCount} msgLen={MsgLen}",
            trace.TraceId,
            workspaceId,
            req.AgentId ?? "(none)",
            req.Audience ?? "agent",
            req.TargetAgentIds?.Count ?? 0,
            req.MessageText?.Length ?? 0);
        await telemetry.RecordTimelineAsync(
            trace,
            RuntimeActivityComponents.AgentExecution,
            "chat.post.received",
            "chat.send",
            RuntimeActivityStatuses.Started,
            metadata: new Dictionary<string, string>
            {
                ["agentId"] = req.AgentId ?? "",
                ["audience"] = req.Audience ?? "agent",
                ["targetCount"] = (req.TargetAgentIds?.Count ?? 0).ToString(),
                ["messageChars"] = (req.MessageText?.Length ?? 0).ToString(),
            },
            ct: ct);
        await telemetry.RecordTelemetryMetricAsync(
            trace,
            TelemetryMetricCategories.Session,
            "session.message.received",
            TelemetryMetricStatuses.Started,
            durationMs: null,
            countValue: 1,
            dimensions: new Dictionary<string, string>
            {
                ["agent_id"] = req.AgentId ?? "",
                ["audience"] = req.Audience ?? "agent",
                ["target_count"] = (req.TargetAgentIds?.Count ?? 0).ToString(),
                ["message_chars"] = (req.MessageText?.Length ?? 0).ToString(),
            },
            ct: ct);

        // 验证 workspace 存在
        var ws = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null)
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        if (string.IsNullOrWhiteSpace(req.MessageText))
            return BadRequest(new { message = "消息内容不能为空" });

        var userExternalId = ResolveCurrentUserId();
        SystemCommand? pipelineSystemCommand = null;
        var trimmedMessage = req.MessageText.Trim();
        if (trimmedMessage.StartsWith("/", StringComparison.Ordinal))
        {
            if (!SystemCommandParser.TryParse(trimmedMessage, out var parsedCommand)
                || parsedCommand.CommandKind != SystemCommandKind.Authorization)
            {
                return await systemCommands.HandleSystemCommandAsync(
                    workspaceId,
                    req,
                    trace,
                    userExternalId,
                    ct);
            }

            pipelineSystemCommand = parsedCommand;
        }

        var acceptDecision = runtimeControl.CanAcceptUserMessage(req.SessionId);
        if (!acceptDecision.Allowed && pipelineSystemCommand is null)
        {
            return await systemCommands.HandleEngineResponseAsync(
                workspaceId,
                req,
                trace,
                acceptDecision.Message,
                sourceType: "runtime_control",
                sourceName: "Runtime Control",
                ct);
        }

        logger.LogInformation(
            "[Chat] DB_LOAD_AGENTS start ws={WorkspaceId}",
            workspaceId);
        var workspaceAgents = await ChatDispatchService.LoadWorkspaceAgentsForRoutingAsync(db, workspaceAgentFileService, ws.Id, workspaceId, ct);
        logger.LogInformation(
            "[Chat] DB_LOAD_AGENTS done ws={WorkspaceId} count={Count} elapsedMs={ElapsedMs}",
            workspaceId, workspaceAgents.Count, sw.ElapsedMilliseconds);
        var route = ChatRoomRouteResolver.Resolve(
            req,
            workspaceAgents);
        if (route.PrimaryAgentId is null || route.TargetAgentIds.Count == 0)
            return BadRequest(new { message = "当前工作区没有可接收消息的 Agent" });

        req = req with
        {
            MessageText = route.MessageText,
            OriginalMessageText = route.OriginalMessageText,
            AgentId = route.PrimaryAgentId,
            Audience = route.Audience,
            TargetAgentIds = route.TargetAgentIds,
        };

        if (pipelineSystemCommand is not null
            && pipelineSystemCommand.CommandKind != SystemCommandKind.Compact)
        {
            var pipelineSessionId = req.SessionId ?? Guid.NewGuid().ToString("N");
            var commandResult = await systemCommands.ProcessSystemCommandAsync(
                pipelineSystemCommand,
                workspaceId,
                pipelineSessionId,
                req.AgentId ?? string.Empty,
                userExternalId,
                ct);

            if (!commandResult.ContinueToAgent)
            {
                return await systemCommands.HandleSystemCommandAsync(
                    workspaceId,
                    req,
                    trace,
                    userExternalId,
                    ct);
            }

            req = req with
            {
                SessionId = pipelineSessionId,
                MessageText = commandResult.AgentMessageText,
                OriginalMessageText = route.OriginalMessageText,
            };

            logger.LogInformation(
                "[Chat:SystemCommandPipeline] command={Command} agent={AgentId} session={SessionId} resultLen={ResultLen}",
                pipelineSystemCommand.RawText,
                req.AgentId,
                pipelineSessionId,
                commandResult.Message.Length);
        }

        logger.LogInformation(
            "[Chat] ROUTE trace={TraceId} ws={WorkspaceId} primary={AgentId} audience={Audience} targets={Targets} runtimeLen={RuntimeLen} originalLen={OriginalLen}",
            trace.TraceId,
            workspaceId,
            req.AgentId,
            req.Audience,
            string.Join(",", req.TargetAgentIds),
            req.MessageText.Length,
            req.OriginalMessageText?.Length ?? 0);
        await telemetry.RecordTimelineAsync(
            trace,
            RuntimeActivityComponents.AgentExecution,
            "chat.route.resolved",
            "chat.route",
            RuntimeActivityStatuses.Succeeded,
            metadata: new Dictionary<string, string>
            {
                ["primaryAgentId"] = req.AgentId ?? "",
                ["audience"] = req.Audience ?? "",
                ["targets"] = string.Join(",", req.TargetAgentIds),
                ["runtimeMessageChars"] = req.MessageText.Length.ToString(),
            },
            ct: ct);

        // 使用 web-chat 内置渠道 ID（已在 SeedDefaults 中注册）
        var channelId = $"web-chat-{workspaceId}";

        logger.LogInformation(
            "[Chat] DISPATCH_RESOLVE start ws={WorkspaceId} targets={Targets}",
            workspaceId, string.Join(",", req.TargetAgentIds));
        var dispatches = new List<ChatAgentDispatch>();
        foreach (var targetAgentId in req.TargetAgentIds)
        {
            var dispatchSw = System.Diagnostics.Stopwatch.StartNew();
            dispatches.Add(await dispatch.ResolveChatAgentDispatchAsync(
                workspaceId,
                ws.Id,
                targetAgentId,
                ct));
            logger.LogInformation(
                "[Chat] DISPATCH_RESOLVE agent={AgentId} elapsedMs={ElapsedMs}",
                targetAgentId, dispatchSw.ElapsedMilliseconds);
        }
        logger.LogInformation(
            "[Chat] DISPATCH_RESOLVE done ws={WorkspaceId} count={Count} elapsedMs={ElapsedMs}",
            workspaceId, dispatches.Count, sw.ElapsedMilliseconds);

        var primaryDispatch = dispatches[0];
        if (string.IsNullOrWhiteSpace(req.SessionId)
            && !req.ForceNewSession
            && dispatches.Count == 1
            && !string.Equals(req.Audience, "all", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "[Chat] ENSURE_MAIN_SESSION start agent={AgentId}",
                primaryDispatch.AgentId);
            var ensureSw = System.Diagnostics.Stopwatch.StartNew();
            var main = await apiClient.EnsureMainSessionAsync(new Services.EnsureMainSessionRequest
            {
                WorkspaceId = workspaceId,
                PrincipalKind = "agent",
                PrincipalId = primaryDispatch.AgentId,
                AgentTemplateId = string.IsNullOrWhiteSpace(primaryDispatch.AgentTemplateId)
                    ? $"global:{primaryDispatch.AgentId}"
                    : primaryDispatch.AgentTemplateId,
                Title = primaryDispatch.DisplayName,
            }, ct);

            logger.LogInformation(
                "[Chat] ENSURE_MAIN_SESSION done agent={AgentId} result={Result} elapsedMs={ElapsedMs}",
                primaryDispatch.AgentId,
                main is not null ? "ok" : "null",
                ensureSw.ElapsedMilliseconds);

            if (main is not null)
            {
                // Follow redirect chain (compaction creates new session → old Main redirects to new)
                var resolvedSessionId = redirectStore.Resolve(main.SessionId, workspaceId, primaryDispatch.AgentId);
                if (!string.Equals(resolvedSessionId, main.SessionId, StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "[Chat] Main session redirected old={Old} → new={New}",
                        main.SessionId,
                        resolvedSessionId);
                }

                req = req with { SessionId = resolvedSessionId };
                trace = trace.WithSession(resolvedSessionId, workspaceId);
                traceAccessor.Current = trace;
                await telemetry.RecordTimelineAsync(
                    trace,
                    RuntimeActivityComponents.AgentExecution,
                    "chat.main_session.resolved",
                    "chat.route",
                    RuntimeActivityStatuses.Succeeded,
                    metadata: new Dictionary<string, string>
                    {
                        ["agentId"] = primaryDispatch.AgentId,
                        ["sessionId"] = resolvedSessionId,
                    },
                    ct: ct);
            }
        }

        var initialSessionTitle = await dispatch.ResolveInitialSessionTitleAsync(
            workspaceId,
            req,
            primaryDispatch,
            ct);

        if (pipelineSystemCommand is { CommandKind: SystemCommandKind.Compact })
        {
            // Don't execute compaction synchronously — it blocks the HTTP request
            // for 60s+ and times out. The frontend already calls
            // POST /api/sessions/{sid}/compact (SessionEventsController.Compact)
            // which has proper SSE events, new session creation, and redirect.
            var compactSessionId = req.SessionId ?? Guid.NewGuid().ToString("N");
            var messageId = Guid.NewGuid().ToString("N");
            logger.LogInformation(
                "[Chat:Compact] Forwarding /compact to frontend compact flow session={SessionId}",
                compactSessionId);
            return Ok(new
            {
                messageId,
                sessionId = compactSessionId,
                compactQueued = true,
                hint = "The frontend will now trigger context compaction via the standard compact API."
            });
        }

        if (ChatMessageExecutionService.IsCameraVisualReasoningRequest(req))
        {
            return await messageExecution.StartCameraVisualReasoning(
                workspaceId,
                req,
                primaryDispatch,
                userExternalId,
                trace);
        }

        // ── ADR-056: 命令队列路径 ──────────────────────────
        // 所有消息统一持久化到命令队列，由 ChatExecutionWorker 异步执行。
        var clientRequestId = req.ClientRequestId
            ?? Guid.NewGuid().ToString("N");

        var result = await acceptanceService.AcceptAsync(
            workspaceId,
            req,
            primaryDispatch,
            dispatches.Count,
            channelId,
            userExternalId,
            clientRequestId,
            ct);

        logger.LogInformation(
            "[Chat:Queue] Command accepted cmd={CommandId} turn={TurnId} session={SessionId} status={Status}",
            result.CommandId, result.TurnId, result.SessionId, result.Status);

        sw.Stop();
        return StatusCode(202, new
        {
            status = result.Status,
            commandId = result.CommandId,
            messageId = result.MessageId,
            turnId = result.TurnId,
            sessionId = result.SessionId,
            eventCursor = result.EventCursor?.ToString(),
            clientRequestId = result.ClientRequestId,
        });
    }

    // POST /api/workspaces/{workspaceId}/chat/sessions/{sessionId}/steering
    // Runtime steering bypasses the normal busy-session message gate. The Agent loop consumes it
    // before the next LLM call and injects it into the in-memory context.
    [HttpPost("sessions/{sessionId}/steering")]
    public async Task<ActionResult<AdminChatSteeringResponse>> CreateSteeringMessage(
        string workspaceId,
        string sessionId,
        [FromBody] AdminChatSteeringRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { message = "SessionId 不能为空" });
        if (string.IsNullOrWhiteSpace(req.MessageText))
            return BadRequest(new { message = "引导消息不能为空" });

        var workspaceExists = await db.Workspaces.AsNoTracking()
            .AnyAsync(w => w.WorkspaceId == workspaceId, ct);
        if (!workspaceExists)
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        var entity = await steeringService.CreateAsync(new CreateSessionSteeringMessage(
            WorkspaceId: workspaceId,
            SessionId: sessionId,
            AgentId: req.AgentId,
            MessageText: req.MessageText,
            SourceQueueItemId: req.SourceQueueItemId,
            CreatedBy: ResolveCurrentUserId(),
            Priority: req.Priority), ct);

        var trace = traceAccessor.Current?.WithSession(sessionId, workspaceId)
            ?? RuntimeTraceContext.CreateNew(sessionId: sessionId, workspaceId: workspaceId, userId: ResolveCurrentUserId());

        await ssm.AppendAsync(
            sessionId,
            workspaceId,
            ServerSentEventFrame.Json("steering.created", new
            {
                steeringId = entity.SteeringId,
                sessionId = entity.SessionId,
                workspaceId = entity.WorkspaceId,
                agentId = entity.AgentId,
                status = entity.Status,
                priority = entity.Priority,
                createdAt = entity.CreatedAtUtc.ToUnixTimeMilliseconds(),
            }),
            ct,
            trace,
            RuntimeActivityComponents.AgentExecution,
            "steering.created");
        await telemetry.RecordTelemetryMetricAsync(
            trace,
            TelemetryMetricCategories.Session,
            "session.steering.created",
            TelemetryMetricStatuses.Recorded,
            durationMs: null,
            countValue: 1,
            dimensions: new Dictionary<string, string>
            {
                ["steering_id"] = entity.SteeringId,
                ["agent_id"] = entity.AgentId ?? "",
                ["source_queue_item_id"] = entity.SourceQueueItemId ?? "",
                ["priority"] = entity.Priority.ToString(),
                ["message_chars"] = entity.MessageText.Length.ToString(),
            },
            occurredAtUtc: entity.CreatedAtUtc,
            ct: ct);

        return Ok(new AdminChatSteeringResponse(
            entity.SteeringId,
            entity.SessionId,
            entity.WorkspaceId,
            entity.AgentId,
            entity.Status,
            entity.CreatedAtUtc.ToUnixTimeMilliseconds()));
    }

    private string ResolveCurrentUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? User.Identity?.Name
           ?? "admin";

    internal static Dictionary<string, string>? BuildChatIngressMetadata(
        AdminChatRequest req,
        ChatAgentDispatch dispatch,
        int fanoutIndex,
        int fanoutCount,
        string? turnId = null,
        string? clientRequestId = null)
        => ChatMessageExecutionService.BuildChatIngressMetadata(req, dispatch, fanoutIndex, fanoutCount, turnId, clientRequestId);
}

public sealed record ChatAgentDispatch(
    string AgentId,
    string DisplayName,
    string? AvatarUrl,
    string? AgentTemplateId,
    string? PreferredProviderId,
    LlmConfig? LlmConfig,
    CapabilityPolicy? CapabilityPolicy,
    IReadOnlyList<LlmToolDefinition>? ToolDefinitions,
    IReadOnlyList<SkillPackageInfo>? SkillPackages);
