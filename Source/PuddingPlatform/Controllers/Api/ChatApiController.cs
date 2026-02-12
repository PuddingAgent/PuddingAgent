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
    IServiceScopeFactory scopeFactory,
    ChatTranscriptWriter transcriptWriter,
    MessageTopicService messageTopicService,
    IDbContextFactory<MemoryDbContext> memoryDbFactory,
    PuddingCode.Services.JsonlSessionWriter jsonlWriter,
    ISessionStateManager ssm,
    IRuntimeTraceAccessor traceAccessor,
    TokenUsageRecorder tokenUsageRecorder,
    ChatVisualReasoningSessionRunner visualReasoningRunner,
    SessionTitleService sessionTitleService,
    ISessionTimelineRecorder timelineRecorder,
    ITelemetryMetricSink telemetrySink,
    IPuddingToolCatalogService toolCatalog,
    IToolPermissionPolicyService toolPermissionPolicy,
    IToolAuthorizationService toolAuthorizationService,
    IContextCompactionService contextCompactionService,
    IRuntimeControlService runtimeControl,
    SessionSteeringService steeringService,
    ILlmConfigService llmConfigService,
    IHostApplicationLifetime appLifetime,
    SessionRedirectStore redirectStore,
    ILogger<ChatApiController> logger) : ControllerBase
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
        await RecordTimelineAsync(
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
        await RecordTelemetryMetricAsync(
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
                return await HandleSystemCommandAsync(
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
            return await HandleEngineResponseAsync(
                workspaceId,
                req,
                trace,
                acceptDecision.Message,
                sourceType: "runtime_control",
                sourceName: "Runtime Control",
                ct);
        }

        var workspaceAgents = await LoadWorkspaceAgentsForRoutingAsync(db, workspaceAgentFileService, ws.Id, workspaceId, ct);
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
            var commandResult = await ProcessSystemCommandAsync(
                pipelineSystemCommand,
                workspaceId,
                pipelineSessionId,
                req.AgentId ?? string.Empty,
                userExternalId,
                ct);

            if (!commandResult.ContinueToAgent)
            {
                return await HandleSystemCommandAsync(
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
        await RecordTimelineAsync(
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

        var dispatches = new List<ChatAgentDispatch>();
        foreach (var targetAgentId in req.TargetAgentIds)
        {
            dispatches.Add(await ResolveChatAgentDispatchAsync(
                db,
                workspaceAgentFileService,
                templateFileService,
                minio,
                logger,
                workspaceId,
                ws.Id,
                targetAgentId,
                ct));
        }

        var primaryDispatch = dispatches[0];
        if (string.IsNullOrWhiteSpace(req.SessionId)
            && !req.ForceNewSession
            && dispatches.Count == 1
            && !string.Equals(req.Audience, "all", StringComparison.OrdinalIgnoreCase))
        {
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
                await RecordTimelineAsync(
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

        var initialSessionTitle = await ResolveInitialSessionTitleAsync(
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

        if (IsCameraVisualReasoningRequest(req))
        {
            return StartCameraVisualReasoning(
                workspaceId,
                req,
                primaryDispatch,
                userExternalId,
                trace);
        }

        // T-102: 通过流式接口获取第一个 metadata 帧提取 sessionId/messageId，
        // 然后 fire-and-forget 将剩余帧写入 SSM (EventHub)，立即返回 IDs 给前端。
        var primaryTrace = trace.WithAgent(primaryDispatch.AgentId, primaryDispatch.AgentTemplateId);
        string? streamSessionId = req.SessionId;
        string? streamMessageId = null;
        var framesWritten = 0;
        var userCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var transcriptMessageText = string.IsNullOrWhiteSpace(req.OriginalMessageText)
            ? req.MessageText
            : req.OriginalMessageText;
        var ingressMetadata = BuildChatIngressMetadata(req, primaryDispatch, fanoutIndex: 0, fanoutCount: dispatches.Count);
        var secondaryDispatches = dispatches.Skip(1).ToList();
        var secondaryFanoutStarted = false;

        // 启动后台执行 — 获取第一个 metadata 帧后返回，剩余帧推入 SSM
        await RecordTimelineAsync(
            trace,
            RuntimeActivityComponents.AgentExecution,
            "chat.dispatch.queued",
            "chat.dispatch",
            RuntimeActivityStatuses.Started,
            metadata: new Dictionary<string, string>
            {
                ["primaryAgentId"] = primaryDispatch.AgentId,
                ["fanoutCount"] = dispatches.Count.ToString(),
            },
            ct: ct);
        _ = Task.Run(async () =>
        {
            var backgroundStartedAt = DateTimeOffset.UtcNow;
            var backgroundSw = System.Diagnostics.Stopwatch.StartNew();
            var replyBuilder = new StringBuilder();
            var thinkingChunks = new List<TranscriptThinkingChunk>();
            string? latestUsageJson = null;
            var userTranscriptPersisted = false;
            var assistantTranscriptPersisted = false;

            try
            {
                logger.LogInformation(
                    "[Chat:Stream] Dispatching LLM request agent={Agent} template={Template} hasLlmConfig={HasCfg} provider={Provider} model={Model} endpoint={Endpoint}",
                    primaryDispatch.AgentId, primaryDispatch.AgentTemplateId,
                    primaryDispatch.LlmConfig is not null,
                    primaryDispatch.PreferredProviderId ?? "(none)",
                    primaryDispatch.LlmConfig?.ModelId ?? "(none)",
                    primaryDispatch.LlmConfig?.Endpoint ?? "(none)");

                await foreach (var frame in apiClient.SendMessageStreamAsync(
                    channelId:      channelId,
                    userExternalId: userExternalId,
                    messageText:    req.MessageText,
                    workspaceId:    workspaceId,
                    sessionId:      req.SessionId,
                    llmConfig:      primaryDispatch.LlmConfig,
                    agentTemplateId: primaryDispatch.AgentTemplateId,
                    agentInstanceId: primaryDispatch.AgentId,
                    capabilityPolicy: primaryDispatch.CapabilityPolicy,
                    toolDefinitions: primaryDispatch.ToolDefinitions,
                    skillPackages: primaryDispatch.SkillPackages,
                    forceNewSession: req.ForceNewSession,
                    sessionTitle: initialSessionTitle,
                    metadata: ingressMetadata,
                    ct:             CancellationToken.None))
                {
                    // 提取 metadata 帧中的 IDs
                    if (frame.Event == "metadata")
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(frame.Data);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("sessionId", out var sid))
                                streamSessionId = sid.GetString();
                            if (root.TryGetProperty("messageId", out var mid))
                                streamMessageId = mid.GetString();
                        }
                        catch { /* ignore parse errors */ }
                        if (streamSessionId is not null)
                        {
                            await RecordTimelineAsync(
                                primaryTrace.WithSession(streamSessionId, workspaceId),
                                RuntimeActivityComponents.AgentExecution,
                                "chat.metadata.received",
                                "chat.metadata",
                                RuntimeActivityStatuses.Succeeded,
                                metadata: new Dictionary<string, string>
                                {
                                    ["messageId"] = streamMessageId ?? "",
                                    ["frameDataChars"] = frame.Data.Length.ToString(),
                                },
                                ct: CancellationToken.None);
                        }
                    }

                    // ADR-031: ChatMessages 是面向 UI/检索的聊天转录物化视图。
                    // 用户消息在确认 sessionId 后写入；执行事实仍由 session_event_log 记录。
                    if (!req.SuppressUserTranscript && !userTranscriptPersisted && streamSessionId is not null)
                    {
                        await transcriptWriter.PersistMessageAsync(
                            streamSessionId,
                            role: "user",
                            content: transcriptMessageText,
                            createdAt: userCreatedAt,
                            thinkingJson: null,
                            usageJson: null,
                            workspaceId: workspaceId,
                            agentInstanceId: primaryDispatch.AgentId,
                            agentTemplateId: primaryDispatch.AgentTemplateId,
                            ct: CancellationToken.None);
                        userTranscriptPersisted = true;

                        // 话题检测：若消息以 # 开头则存储话题索引
                        var topic = MessageTopicService.DetectTopic(transcriptMessageText);
                        if (topic is not null)
                        {
                            await messageTopicService.SaveTopicAsync(
                                messageId: 0, // 由 SaveTopicAsync 内部按 session+content 反查
                                topicTitle: topic,
                                sessionId: streamSessionId,
                                workspaceId: workspaceId,
                                ct: CancellationToken.None);
                        }
                    }

                    if (frame.Event == "delta")
                    {
                        var delta = TryReadStringProperty(frame.Data, "delta");
                        if (!string.IsNullOrEmpty(delta))
                            replyBuilder.Append(delta);
                    }
                    else if (frame.Event == "thinking")
                    {
                        var delta = TryReadStringProperty(frame.Data, "delta");
                        if (!string.IsNullOrEmpty(delta))
                        {
                            thinkingChunks.Add(new TranscriptThinkingChunk(
                                delta,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                        }
                    }
                    else if (frame.Event == "usage")
                    {
                        latestUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                    }

                    // ExecuteStreamAsync 已将执行帧写入 SSM；Platform 只补 metadata，避免重复 delta/thinking/done。
                    if (frame.Event == "metadata" && streamSessionId is not null)
                    {
                        var frameTrace = primaryTrace.WithSession(streamSessionId, workspaceId);
                        await ssm.AppendAsync(
                            streamSessionId,
                            workspaceId,
                            frame,
                            CancellationToken.None,
                            frameTrace,
                            RuntimeActivityComponents.AgentExecution,
                            $"chat.stream.{frame.Event}");
                        framesWritten++;

                        if (!secondaryFanoutStarted && secondaryDispatches.Count > 0)
                        {
                            secondaryFanoutStarted = true;
                            var capturedSessionId = streamSessionId;
                            for (var i = 0; i < secondaryDispatches.Count; i++)
                            {
                                var secondaryDispatch = secondaryDispatches[i];
                                var fanoutIndex = i + 1;
                                _ = Task.Run(() => RunSecondaryChatFanoutAsync(
                                    apiClient,
                                    transcriptWriter,
                                    ssm,
                                    tokenUsageRecorder,
                                    logger,
                                    trace.WithAgent(secondaryDispatch.AgentId, secondaryDispatch.AgentTemplateId),
                                    channelId,
                                    userExternalId,
                                    workspaceId,
                                    req,
                                    secondaryDispatch,
                                    capturedSessionId,
                                    fanoutIndex,
                                    dispatches.Count,
                                    CancellationToken.None));
                            }
                        }
                    }

                    // T-CACHE-005: 检测 done 帧，fire-and-forget 更新 TokenUsageStats
                    if (frame.Event == "done" && !string.IsNullOrEmpty(frame.Data))
                    {
                        if (!assistantTranscriptPersisted && streamSessionId is not null)
                        {
                            var reply = TryReadStringProperty(frame.Data, "reply");
                            var assistantContent = !string.IsNullOrWhiteSpace(reply)
                                ? reply
                                : replyBuilder.ToString();
                            var doneUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                            var thinkingJson = thinkingChunks.Count > 0
                                ? JsonSerializer.Serialize(thinkingChunks, JsonOpts)
                                : null;

                            await transcriptWriter.PersistMessageAsync(
                                streamSessionId,
                                role: "agent",
                                content: assistantContent,
                                createdAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                thinkingJson,
                                doneUsageJson,
                                workspaceId: workspaceId,
                                agentInstanceId: primaryDispatch.AgentId,
                                agentTemplateId: primaryDispatch.AgentTemplateId,
                                ct: CancellationToken.None);
                            assistantTranscriptPersisted = true;
                        }

                        var capturedProviderId = primaryDispatch.PreferredProviderId;
                        var capturedModelId = primaryDispatch.LlmConfig?.ModelId;
                        var capturedData = frame.Data;
                        var msgId = streamMessageId ?? streamSessionId?.ToString();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(capturedData);
                                var root = doc.RootElement;
                                if (!root.TryGetProperty("usage", out var usageEl)) return;

                                var usageTokens = JsonSerializer.Deserialize<TokenUsageDto>(usageEl.GetRawText(), JsonOpts);
                                if (usageTokens is null) return;
                                PromptPrefixSnapshot? prefixSnapshot = null;
                                if (root.TryGetProperty("prefixSnapshot", out var prefixEl)
                                    && prefixEl.ValueKind == JsonValueKind.Object)
                                {
                                    prefixSnapshot = JsonSerializer.Deserialize<PromptPrefixSnapshot>(
                                        prefixEl.GetRawText(),
                                        JsonOpts);
                                }

                                var sourceId = msgId ?? Guid.NewGuid().ToString("N");
                                await tokenUsageRecorder.RecordAsync(
                                    usageTokens,
                                    sourceType: "chat_message",
                                    sourceId: sourceId,
                                    workspaceId: workspaceId,
                                    sessionId: streamSessionId?.ToString(),
                                    providerId: capturedProviderId,
                                    modelId: capturedModelId,
                                    prefixSnapshot: prefixSnapshot);
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "[Chat:Stats] Failed to record token usage via recorder");
                            }
                        });
                    }
                }

                logger.LogInformation(
                    "[Chat:FireAndForget] Stream completed ws={Workspace} session={Session} framesWritten={Frames}",
                    workspaceId, streamSessionId, framesWritten);
                backgroundSw.Stop();
                await RecordTimelineAsync(
                    streamSessionId is null ? trace : trace.WithSession(streamSessionId, workspaceId),
                    RuntimeActivityComponents.AgentExecution,
                    "chat.background.completed",
                    "chat.dispatch",
                    RuntimeActivityStatuses.Succeeded,
                    metadata: new Dictionary<string, string>
                    {
                        ["framesWritten"] = framesWritten.ToString(),
                        ["messageId"] = streamMessageId ?? "",
                    },
                    ct: CancellationToken.None);
                await RecordTelemetryMetricAsync(
                    streamSessionId is null ? trace : trace.WithSession(streamSessionId, workspaceId),
                    TelemetryMetricCategories.Session,
                    "session.background.completed",
                    TelemetryMetricStatuses.Succeeded,
                    durationMs: backgroundSw.ElapsedMilliseconds,
                    countValue: 1,
                    occurredAtUtc: backgroundStartedAt,
                    dimensions: new Dictionary<string, string>
                    {
                        ["frames_written"] = framesWritten.ToString(),
                        ["message_id"] = streamMessageId ?? "",
                        ["reply_chars"] = replyBuilder.Length.ToString(),
                        ["thinking_chunks"] = thinkingChunks.Count.ToString(),
                    },
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                backgroundSw.Stop();
                logger.LogWarning(ex,
                    "[Chat:FireAndForget] Background stream failed ws={Workspace} session={Session}",
                    workspaceId, streamSessionId);
                await RecordTimelineAsync(
                    streamSessionId is null ? trace : trace.WithSession(streamSessionId, workspaceId),
                    RuntimeActivityComponents.AgentExecution,
                    "chat.background.failed",
                    "chat.dispatch",
                    RuntimeActivityStatuses.Failed,
                    errorMessage: ex.Message,
                    ct: CancellationToken.None);
                await RecordTelemetryMetricAsync(
                    streamSessionId is null ? trace : trace.WithSession(streamSessionId, workspaceId),
                    TelemetryMetricCategories.Session,
                    "session.background.failed",
                    TelemetryMetricStatuses.Failed,
                    durationMs: backgroundSw.ElapsedMilliseconds,
                    countValue: 1,
                    occurredAtUtc: backgroundStartedAt,
                    dimensions: new Dictionary<string, string>
                    {
                        ["frames_written"] = framesWritten.ToString(),
                        ["message_id"] = streamMessageId ?? "",
                    },
                    error: ex,
                    ct: CancellationToken.None);
            }
        });

        // 等待首个 metadata 帧到达（带超时）
        await RecordTimelineAsync(
            trace,
            RuntimeActivityComponents.AgentExecution,
            "chat.metadata.wait.started",
            "chat.metadata.wait",
            RuntimeActivityStatuses.Started,
            ct: ct);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            while (streamMessageId is null && streamSessionId is null)
            {
                await Task.Delay(100, linkedCts.Token);
                // streamSessionId 可能从 request 中已有
                if (req.SessionId is not null && streamSessionId is null)
                    streamSessionId = req.SessionId;
            }

            // 再等一等 messageId（metadata 帧可能延迟）
            var waitStart = System.Diagnostics.Stopwatch.StartNew();
            while (streamMessageId is null && waitStart.ElapsedMilliseconds < 3000)
            {
                await Task.Delay(100, linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "[Chat:FireAndForget] Timeout waiting for metadata ws={Workspace} session={Session}",
                workspaceId, streamSessionId);
        }

        sw.Stop();

        // P0-2 修复: metadata 帧获取失败时返回 500，防止前端 loading 永久悬挂
        if (streamMessageId is null || streamSessionId is null)
        {
            logger.LogError(
                "[Chat:FireAndForget] Failed to get metadata ws={Workspace} msgId={MessageId} session={Session} elapsed={Elapsed}ms",
                workspaceId, streamMessageId, streamSessionId, sw.ElapsedMilliseconds);
            await RecordTimelineAsync(
                trace,
                RuntimeActivityComponents.AgentExecution,
                "chat.post.failed",
                "chat.send",
                RuntimeActivityStatuses.Failed,
                durationMs: sw.ElapsedMilliseconds,
                errorMessage: "metadata timeout",
                ct: CancellationToken.None);
            await RecordTelemetryMetricAsync(
                trace,
                TelemetryMetricCategories.Session,
                "session.message.failed",
                TelemetryMetricStatuses.Failed,
                durationMs: sw.ElapsedMilliseconds,
                countValue: 1,
                dimensions: new Dictionary<string, string>
                {
                    ["reason"] = "metadata_timeout",
                    ["agent_id"] = req.AgentId ?? "",
                },
                errorMessage: "metadata timeout",
                ct: CancellationToken.None);
            return StatusCode(500, new { message = "AI 服务响应超时，请稍后重试" });
        }

        logger.LogInformation(
            "[Chat:FireAndForget] Returned ws={Workspace} msgId={MessageId} sessionId={SessionId} elapsed={Elapsed}ms",
            workspaceId, streamMessageId, streamSessionId, sw.ElapsedMilliseconds);
        await RecordTimelineAsync(
            trace.WithSession(streamSessionId, workspaceId),
            RuntimeActivityComponents.AgentExecution,
            "chat.post.returned",
            "chat.send",
            RuntimeActivityStatuses.Succeeded,
            durationMs: sw.ElapsedMilliseconds,
            metadata: new Dictionary<string, string>
            {
                ["messageId"] = streamMessageId,
            },
            ct: CancellationToken.None);
        await RecordTelemetryMetricAsync(
            trace.WithSession(streamSessionId, workspaceId),
            TelemetryMetricCategories.Session,
            "session.message.returned",
            TelemetryMetricStatuses.Succeeded,
            durationMs: sw.ElapsedMilliseconds,
            countValue: 1,
            dimensions: new Dictionary<string, string>
            {
                ["message_id"] = streamMessageId,
                ["agent_id"] = req.AgentId ?? "",
            },
            ct: CancellationToken.None);

        return Ok(new { messageId = streamMessageId, sessionId = streamSessionId });
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
        await RecordTelemetryMetricAsync(
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

    private async Task<IActionResult> HandleSystemCommandAsync(
        string workspaceId,
        AdminChatRequest req,
        RuntimeTraceContext trace,
        string userExternalId,
        CancellationToken ct)
    {
        var chatSessionId = req.SessionId ?? Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");
        var userCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var responseCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var trimmedCommand = req.MessageText.Trim();
        var frameTrace = trace.WithSession(chatSessionId, workspaceId).WithAgent(req.AgentId, req.AgentId);

        var responseText = await BuildSystemCommandResponseAsync(
            trimmedCommand,
            workspaceId,
            chatSessionId,
            req.AgentId ?? string.Empty,
            userExternalId,
            ct);

        return await HandleEngineResponseAsync(
            workspaceId,
            req,
            trace,
            responseText,
            sourceType: "system_command",
            sourceName: "System",
            ct,
            chatSessionId,
            messageId,
            userCreatedAt,
            responseCreatedAt,
            trimmedCommand);
    }

    private async Task<IActionResult> HandleEngineResponseAsync(
        string workspaceId,
        AdminChatRequest req,
        RuntimeTraceContext trace,
        string responseText,
        string sourceType,
        string sourceName,
        CancellationToken ct,
        string? chatSessionId = null,
        string? messageId = null,
        long? userCreatedAt = null,
        long? responseCreatedAt = null,
        string? userMessageText = null)
    {
        chatSessionId ??= req.SessionId ?? Guid.NewGuid().ToString("N");
        messageId ??= Guid.NewGuid().ToString("N");
        userCreatedAt ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        responseCreatedAt ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        userMessageText ??= req.MessageText.Trim();
        var frameTrace = trace.WithSession(chatSessionId, workspaceId).WithAgent(req.AgentId, req.AgentId);

        await transcriptWriter.PersistMessageAsync(
            chatSessionId,
            role: "user",
            content: userMessageText,
            createdAt: userCreatedAt.Value,
            thinkingJson: null,
            usageJson: null,
            workspaceId: workspaceId,
            agentInstanceId: req.AgentId,
            agentTemplateId: req.AgentId,
            ct: ct);

        await transcriptWriter.PersistMessageAsync(
            chatSessionId,
            role: "agent",
            content: responseText,
            createdAt: responseCreatedAt.Value,
            thinkingJson: null,
            usageJson: null,
            workspaceId: workspaceId,
            agentInstanceId: req.AgentId,
            agentTemplateId: req.AgentId,
            ct: ct);

        await ssm.AppendAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Metadata, new
            {
                sessionId = chatSessionId,
                messageId,
                agentId = req.AgentId,
                sourceType,
                sourceId = "system",
                sourceName,
            }),
            ct,
            frameTrace,
            RuntimeActivityComponents.AgentExecution,
            "chat.command.metadata");

        await ssm.AppendAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Delta, new
            {
                delta = responseText,
            }),
            ct,
            frameTrace,
            RuntimeActivityComponents.AgentExecution,
            "chat.command.delta");

        await ssm.AppendAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Done, new
            {
                messageId,
                sessionId = chatSessionId,
                reply = responseText,
            }),
            ct,
            frameTrace,
            RuntimeActivityComponents.AgentExecution,
            "chat.command.done");

        await ssm.MarkStreamCompleteAsync(chatSessionId, ct);
        await RecordTimelineAsync(
            frameTrace,
            RuntimeActivityComponents.AgentExecution,
            sourceType == "system_command" ? "chat.system_command.handled" : "chat.runtime_control.handled",
            sourceType == "system_command" ? "chat.command" : "chat.runtime_control",
            RuntimeActivityStatuses.Succeeded,
            metadata: new Dictionary<string, string>
            {
                ["agentId"] = req.AgentId ?? "",
                ["messageLength"] = userMessageText.Length.ToString(),
            },
            ct: ct);
        await RecordTelemetryMetricAsync(
            frameTrace,
            TelemetryMetricCategories.Session,
            "session.system_command.handled",
            TelemetryMetricStatuses.Succeeded,
            durationMs: null,
            countValue: 1,
            dimensions: new Dictionary<string, string>
            {
                ["agent_id"] = req.AgentId ?? "",
                ["message_id"] = messageId,
            },
            ct: ct);

        logger.LogInformation(
            "[Chat:EngineResponse] Handled ws={Workspace} session={Session} agent={AgentId} source={SourceType} message={Message}",
            workspaceId,
            chatSessionId,
            req.AgentId,
            sourceType,
            userMessageText);

        return Ok(new { messageId, sessionId = chatSessionId });
    }

    private async Task<string> BuildSystemCommandResponseAsync(
        string commandText,
        string workspaceId,
        string sessionId,
        string agentId,
        string userExternalId,
        CancellationToken ct)
    {
        if (!SystemCommandParser.TryParse(commandText, out var command))
            return "Unknown system command. Send /help for available commands.";

        var result = await ProcessSystemCommandAsync(
            command,
            workspaceId,
            sessionId,
            agentId,
            userExternalId,
            ct);

        return result.Message;
    }

    private async Task<SystemCommandProcessingResult> ProcessSystemCommandAsync(
        SystemCommand command,
        string workspaceId,
        string sessionId,
        string agentId,
        string userExternalId,
        CancellationToken ct)
    {
        if (command.Action == SystemCommandAction.Help)
        {
            return SystemCommandProcessingResult.Stop(
                ToolAuthorizationDefaults.BuildHelpMessage(command.TargetId));
        }

        if (command.CommandKind == SystemCommandKind.Compact)
        {
            var compactResult = await contextCompactionService.CompactAsync(
                new ContextCompactionRequest(
                    workspaceId,
                    sessionId,
                    string.IsNullOrWhiteSpace(agentId) ? null : agentId,
                    ContextCompactionMode.Manual,
                    ContextCompactionLevel.Full,
                    "user command /compact"),
                ct);

            return SystemCommandProcessingResult.Stop(BuildCompactResultMessage(compactResult));
        }

        if (command.CommandKind == SystemCommandKind.Memory)
        {
            return SystemCommandProcessingResult.Stop(
                "Command '/memory' is recognized, but this feature is not implemented yet.");
        }

        if (command.CommandKind == SystemCommandKind.Status)
        {
            return SystemCommandProcessingResult.Stop(
                BuildStatusMessage(runtimeControl.GetStatus(sessionId), sessionId, agentId));
        }

        if (command.CommandKind == SystemCommandKind.Stop)
        {
            var stopResult = string.Equals(command.TargetId, "all", StringComparison.Ordinal)
                ? runtimeControl.StopAll("user command /stop all")
                : runtimeControl.StopSession(sessionId, "user command /stop");
            return SystemCommandProcessingResult.Stop(stopResult.Message);
        }

        if (command.CommandKind == SystemCommandKind.Mode)
        {
            if (string.Equals(command.TargetId, "list", StringComparison.Ordinal))
                return SystemCommandProcessingResult.Stop(BuildModeListMessage(runtimeControl.Mode));

            if (string.Equals(command.TargetId, "safe", StringComparison.Ordinal))
                return SystemCommandProcessingResult.Stop(
                    runtimeControl.SetMode(RuntimeExecutionMode.Safe, "user command /mode safe").Message);

            if (string.Equals(command.TargetId, "normal", StringComparison.Ordinal))
                return SystemCommandProcessingResult.Stop(
                    runtimeControl.SetMode(RuntimeExecutionMode.Normal, "user command /mode normal").Message);

            return SystemCommandProcessingResult.Stop($"Current runtime mode: {runtimeControl.Mode}. Send /mode list for examples.");
        }

        if (command.CommandKind == SystemCommandKind.Yolo)
        {
            var yoloResult = runtimeControl.SetMode(RuntimeExecutionMode.Yolo, "user command /yolo");
            logger.LogWarning(
                "[Chat:Yolo] YOLO mode activated — all tool permission checks bypassed. Session={SessionId} Agent={AgentId}",
                sessionId, agentId);
            return SystemCommandProcessingResult.Stop(yoloResult.Message);
        }

        if (command.CommandKind == SystemCommandKind.EmergencyStop)
        {
            runtimeControl.SetMode(RuntimeExecutionMode.EmergencyStopping, "user command /estop");
            runtimeControl.StopAll("emergency stop");
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                appLifetime.StopApplication();
            });
            return SystemCommandProcessingResult.Stop(
                "Emergency stop accepted. Runtime is rejecting new messages, cancelling active sessions and stopping the backend.");
        }

        // Resume command handled via raw text since its enum isn't resolved here
        if (command.RawText?.StartsWith("/resume", StringComparison.OrdinalIgnoreCase) == true)
        {
            var sessionToResume = string.IsNullOrWhiteSpace(command.TargetId) || command.TargetId == "resume"
                ? sessionId
                : command.TargetId;
            var resumeResult = runtimeControl.ResetSessionFault(sessionToResume);
            return SystemCommandProcessingResult.Stop(resumeResult.Message);
        }

        var descriptor = toolCatalog.ListTools()
            .FirstOrDefault(t => t.ToolId.Equals(command.TargetId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return SystemCommandProcessingResult.Stop(
                $"Unknown tool '{command.TargetId}'. Send /help for available commands.");
        }

        if (!toolPermissionPolicy.RequiresRuntimeAuthorization(descriptor))
        {
            return SystemCommandProcessingResult.Stop(
                $"Tool '{command.TargetId}' does not require runtime authorization.");
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return SystemCommandProcessingResult.Stop(
                $"Select an agent before authorizing tool '{command.TargetId}'.");
        }

        var result = await toolAuthorizationService.ApplyCommandAsync(
            command.ToToolAuthorizationCommand(),
            new ToolAuthorizationContext
            {
                WorkspaceId = workspaceId,
                SessionId = sessionId,
                AgentInstanceId = agentId,
                UserId = userExternalId,
                ToolId = command.TargetId,
            },
            ct);

        logger.LogInformation(
            "[Chat:SystemCommandAuth] action={Action} tool={ToolId} scope={Scope} workspace={WorkspaceId} session={SessionId} agent={AgentId} user={UserId} message={Message}",
            command.Action,
            command.TargetId,
            command.Scope,
            workspaceId,
            sessionId,
            agentId,
            userExternalId,
            result.Message);

        return SystemCommandProcessingResult.Continue(
            result.Message,
            BuildAgentSystemCommandMessage(command, result.Message));
    }

    private static string BuildAgentSystemCommandMessage(
        SystemCommand command,
        string engineMessage)
    {
        var scopeText = command.Action == SystemCommandAction.Authorize
            ? command.Scope switch
            {
                ToolAuthorizationScope.Once => "once",
                ToolAuthorizationScope.Session => "session",
                ToolAuthorizationScope.Permanent => "permanent",
                _ => $"{Math.Ceiling(command.Duration.TotalMinutes):0} minutes",
            }
            : "not applicable";

        return
            "The user sent a system command. The execution engine intercepted and processed it before routing this message to you." +
            Environment.NewLine + Environment.NewLine +
            $"User command: `{command.RawText}`" + Environment.NewLine +
            $"Execution engine result: {engineMessage}" + Environment.NewLine +
            $"Command target: `{command.TargetId}`" + Environment.NewLine +
            $"Command action: `{command.Action.ToString().ToLowerInvariant()}`" + Environment.NewLine +
            $"Authorization scope: `{scopeText}`" + Environment.NewLine + Environment.NewLine +
            "Continue the conversation naturally. If this command authorized a tool you need, you may use that tool now.";
    }

    private static string BuildModeListMessage(RuntimeExecutionMode currentMode)
        => "Runtime modes:" + Environment.NewLine + Environment.NewLine +
           $"- Current: `{currentMode}`" + Environment.NewLine +
           "- `normal` - Allow normal Agent and Tool scheduling. Example: `/mode normal`" + Environment.NewLine +
           "- `safe` - Block new Agent messages, Agent starts, and Tool calls. Example: `/mode safe`" + Environment.NewLine +
           "- `yolo` - Bypass all tool permission checks (memory-only, lost on restart). Example: `/yolo`" + Environment.NewLine +
           "- `emergency_stopping` - Backend is shutting down. Trigger: `/estop`";

    private static string BuildCompactResultMessage(ContextCompactionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Context compacted.");
        sb.AppendLine();
        sb.AppendLine($"- Session: `{result.SessionId}`");
        sb.AppendLine($"- Mode: `{result.Mode}`");
        sb.AppendLine($"- Level: `{result.Level}`");
        sb.AppendLine($"- Compacted messages: {result.CompactedMessageCount}");
        sb.AppendLine($"- Tokens: {result.BeforeTokens} -> {result.AfterTokens}");
        if (result.Diagnostics is { } diag)
        {
            sb.AppendLine();
            sb.AppendLine("Diagnostics:");
            sb.AppendLine($"- Compaction ID: `{diag.CompactionId}`");
            sb.AppendLine($"- Previous session: `{diag.PreviousSessionId}`");
            if (!string.IsNullOrWhiteSpace(diag.PreviousLastMessageId))
                sb.AppendLine($"- Previous last message: `{diag.PreviousLastMessageId}`");
            sb.AppendLine($"- Previous session size: {diag.BeforeTokens} tokens / {diag.ActiveMessageCountBefore} messages");
            sb.AppendLine($"- Summary size: {diag.SummaryCharacterCount} chars / {diag.SummaryEstimatedTokens} tokens");
            sb.AppendLine($"- Summary generator: `{diag.SummaryGenerator}`");
            sb.AppendLine($"- Completed at: `{diag.CompletedAtUtc}`");
            sb.AppendLine($"- Duration: {diag.DurationMs} ms");
        }

        if (string.IsNullOrWhiteSpace(result.SummaryPreview))
        {
            sb.AppendLine("- Summary: no eligible messages required compaction.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(result.SummaryPreview);
        }

        return sb.ToString();
    }

    private static string BuildStatusMessage(RuntimeStatusSnapshot snapshot, string sessionId, string agentId)
    {
        var session = snapshot.Session;
        var sb = new StringBuilder();
        sb.AppendLine("Runtime status snapshot:");
        sb.AppendLine();
        sb.AppendLine($"- Runtime: mode={snapshot.Mode}, capturedAt={snapshot.CapturedAtUtc:O}, activeSessions={snapshot.ActiveSessions}");
        sb.AppendLine($"- Session: id={sessionId}, state={session?.State.ToString() ?? "Unknown"}, recentErrors={session?.RecentErrorCount ?? 0}, windowErrors={session?.WindowErrorCount ?? 0}, sameFingerprint={session?.SameFingerprintCount ?? 0}");
        sb.AppendLine($"- Agent: id={(string.IsNullOrWhiteSpace(agentId) ? "(none)" : agentId)}");
        sb.AppendLine("- Model: configured by current Agent template/provider dispatch.");
        sb.AppendLine("- Skill: available through current Agent template and registered tool catalog.");
        sb.AppendLine("- Tool: calls are allowed only when runtime mode is normal and the session is not faulted.");
        sb.AppendLine("- Safety: high-risk tools still require explicit authorization; safe mode blocks all tools.");
        sb.AppendLine("- Resource: token/tool counters are recorded in runtime telemetry and session trace logs.");
        sb.AppendLine("- Recovery: use `/stop`, start a new session, or `/mode normal` after safe mode. Faulted sessions remain blocked.");
        if (!string.IsNullOrWhiteSpace(session?.FaultSummary))
        {
            sb.AppendLine();
            sb.AppendLine(session.FaultSummary);
        }
        if (session?.RecentErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recent errors:");
            foreach (var error in session.RecentErrors.TakeLast(5))
                sb.AppendLine($"- {error.TimestampUtc:O} {error.Kind}/{error.Component}: {error.Message}");
        }
        return sb.ToString();
    }

    private sealed record SystemCommandProcessingResult(
        string Message,
        bool ContinueToAgent,
        string AgentMessageText)
    {
        public static SystemCommandProcessingResult Stop(string message)
            => new(message, ContinueToAgent: false, AgentMessageText: message);

        public static SystemCommandProcessingResult Continue(string message, string agentMessageText)
            => new(message, ContinueToAgent: true, AgentMessageText: agentMessageText);
    }

    private string ResolveCurrentUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? User.Identity?.Name
           ?? "admin";

    private IActionResult StartCameraVisualReasoning(
        string workspaceId,
        AdminChatRequest req,
        ChatAgentDispatch dispatch,
        string userExternalId,
        RuntimeTraceContext trace)
    {
        var chatSessionId = req.SessionId ?? Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");
        var transcriptMessageText = string.IsNullOrWhiteSpace(req.OriginalMessageText)
            ? req.MessageText
            : req.OriginalMessageText;
        var userCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var visualProvider = ResolveVisualProvider(req.Metadata, dispatch.PreferredProviderId);
        var visualModel = ResolveVisualModel(req.Metadata, dispatch.LlmConfig?.ModelId);

        _ = Task.Run(async () =>
        {
            try
            {
                if (!req.SuppressUserTranscript)
                {
                    await transcriptWriter.PersistMessageAsync(
                        chatSessionId,
                        role: "user",
                        content: transcriptMessageText,
                        createdAt: userCreatedAt,
                        thinkingJson: null,
                        usageJson: null,
                        workspaceId: workspaceId,
                        agentInstanceId: dispatch.AgentId,
                        agentTemplateId: dispatch.AgentTemplateId,
                        ct: CancellationToken.None);
                }

                var result = await visualReasoningRunner.RunAsync(new ChatVisualReasoningSessionRunRequest
                {
                    WorkspaceId = workspaceId,
                    RoomId = $"web-chat-{workspaceId}",
                    ParticipantId = userExternalId,
                    AgentId = dispatch.AgentId,
                    AgentDisplayName = dispatch.DisplayName,
                    AvatarUrl = dispatch.AvatarUrl,
                    ChatRequest = req with { SessionId = chatSessionId },
                    Provider = visualProvider,
                    Model = visualModel,
                    SessionId = chatSessionId,
                    MessageId = messageId,
                    Trace = trace.WithSession(chatSessionId, workspaceId).WithAgent(dispatch.AgentId, dispatch.AgentTemplateId),
                }, CancellationToken.None);

                if (result.Success)
                {
                    await transcriptWriter.PersistMessageAsync(
                        chatSessionId,
                        role: "agent",
                        content: result.Reply ?? string.Empty,
                        createdAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        thinkingJson: null,
                        usageJson: null,
                        workspaceId: workspaceId,
                        agentInstanceId: dispatch.AgentId,
                        agentTemplateId: dispatch.AgentTemplateId,
                        ct: CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[Chat:Vision] Background visual reasoning failed ws={Workspace} session={Session} agent={AgentId}",
                    workspaceId,
                    chatSessionId,
                    dispatch.AgentId);
            }
        });

        logger.LogInformation(
            "[Chat:Vision] Returned ws={Workspace} msgId={MessageId} sessionId={SessionId} provider={Provider} model={Model}",
            workspaceId,
            messageId,
            chatSessionId,
            visualProvider,
            visualModel);

        return Ok(new { messageId, sessionId = chatSessionId });
    }

    private async Task RecordTimelineAsync(
        RuntimeTraceContext trace,
        string component,
        string stage,
        string operation,
        string status,
        long? durationMs = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        try
        {
            await timelineRecorder.RecordAsync(new SessionTimelineRecord
            {
                Trace = trace,
                Component = component,
                Stage = stage,
                Operation = operation,
                Status = status,
                DurationMs = durationMs,
                Metadata = metadata,
                ErrorMessage = errorMessage,
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // 可观测性副作用，不得阻断消息主流程；请求取消时静默丢弃该条 timeline。
            logger.LogDebug("[Chat] Timeline record cancelled stage={Stage}", stage);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Chat] Timeline record failed stage={Stage}", stage);
        }
    }

    private async Task RecordTelemetryMetricAsync(
        RuntimeTraceContext trace,
        string category,
        string name,
        string status,
        long? durationMs,
        long? countValue,
        IReadOnlyDictionary<string, string>? dimensions = null,
        DateTimeOffset? occurredAtUtc = null,
        Exception? error = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        try
        {
            await telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "backend",
                Category = category,
                Name = name,
                Status = status,
                OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
                DurationMs = durationMs,
                CountValue = countValue,
                Unit = countValue is null ? null : "event",
                Severity = error is null && status != TelemetryMetricStatuses.Failed ? "info" : "error",
                Summary = name,
                Dimensions = dimensions,
                ErrorCode = error?.GetType().Name,
                ErrorMessage = error?.Message ?? errorMessage,
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // 可观测性副作用，不得阻断消息主流程；请求取消时静默丢弃该条 metric。
            logger.LogDebug("[Chat] Telemetry metric record cancelled name={Name}", name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Chat] Telemetry metric record failed name={Name}", name);
        }
    }

    private async Task<ChatAgentDispatch> ResolveChatAgentDispatchAsync(
        PlatformDbContext db,
        WorkspaceAgentFileService workspaceAgentFileService,
        AgentTemplateFileService templateFileService,
        MinioStorageService minio,
        ILogger<ChatApiController> logger,
        string workspaceId,
        int workspacePk,
        string agentId,
        CancellationToken ct)
    {
        string? agentTemplateId = null;
        string? preferredProviderId = null;
        string? preferredModelId = null;
        string? displayName = null;
        string? avatarUrl = null;

        var fileAgent = await workspaceAgentFileService.GetAgentAsync(workspaceId, agentId, ct);
        var agent = fileAgent is null
            ? await db.WorkspaceAgents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.WorkspaceEntityId == workspacePk && a.AgentId == agentId && a.IsEnabled, ct)
            : null;
        string resolveSource = "none";
        if (fileAgent is not null && fileAgent.IsEnabled)
        {
            agentTemplateId = fileAgent.SourceTemplateId;
            preferredProviderId = fileAgent.PreferredProviderId;
            preferredModelId = fileAgent.PreferredModelId;
            displayName = fileAgent.DisplayName ?? fileAgent.Name;
            avatarUrl = fileAgent.AvatarUrl;
            resolveSource = "file";
        }
        else if (agent is not null)
        {
            agentTemplateId = agent.SourceTemplateId;
            resolveSource = "db";
            displayName = agent.DisplayName ?? agent.Name;
            avatarUrl = agent.AvatarUrl;
        }

        if (string.IsNullOrWhiteSpace(preferredProviderId) && !string.IsNullOrWhiteSpace(agentTemplateId))
        {
            var template = await ResolveAgentTemplateAsync(templateFileService, agentTemplateId, ct);
            if (template is not null)
            {
                preferredProviderId = template.PreferredProviderId;
                preferredModelId = template.PreferredModelId;
                resolveSource = $"{resolveSource}+template";
            }
        }

        logger.LogInformation(
            "[Chat:Dispatch] Agent config resolved agent={AgentId} template={Template} provider={Provider} model={Model} source={Source}",
            agentId, agentTemplateId ?? "(none)",
            preferredProviderId ?? "(null)", preferredModelId ?? "(null)", resolveSource);

        var resolved = await ResolveCapabilitiesAsync(
            db, templateFileService, toolCatalog, toolPermissionPolicy, workspaceId, agentTemplateId, ct);
        logger.LogDebug(
            "[Chat:Tools] Platform resolved tool definitions workspace={WorkspaceId} agent={AgentId} template={TemplateId} source={Source} capabilityCount={CapabilityCount} toolCount={ToolCount} tools={Tools} allowedToolCount={AllowedToolCount} defaultToolCount={DefaultToolCount} grantToolCount={GrantToolCount}",
            workspaceId,
            agentId,
            agentTemplateId ?? "",
            resolved.Source,
            resolved.CapabilityCount,
            resolved.ToolDefinitions?.Count ?? 0,
            SummarizeToolDefinitions(resolved.ToolDefinitions),
            resolved.Policy?.AllowedToolNames.Count ?? 0,
            resolved.Policy?.DefaultToolNames.Count ?? 0,
            resolved.Policy?.RequiresGrantToolNames.Count ?? 0);

        var skillPackages = await ResolveSkillPackagesAsync(db, minio, templateFileService, agentTemplateId, ct);
        LlmConfig? llmConfig = null;
        if (preferredProviderId is not null)
        {
            var normalizedModelId = NormalizePreferredModelId(preferredProviderId, preferredModelId);
            llmConfig = llmConfigService.Resolve(preferredProviderId, normalizedModelId);
            if (llmConfig is not null)
            {
                var templateReasoningEffort = await ResolveReasoningEffortAsync(
                    db, templateFileService, workspaceId, agentTemplateId, ct);
                if (!string.IsNullOrWhiteSpace(templateReasoningEffort))
                    llmConfig = llmConfig with { ReasoningEffort = templateReasoningEffort };

                logger.LogInformation(
                    "[Chat] LlmConfig resolved from agent template: agentId={AgentId} provider={ProviderId} model={ModelId} rawModel={RawModelId} endpoint={Endpoint} hasKeyVaultRef={HasKeyVaultRef}",
                    agentId,
                    preferredProviderId,
                    llmConfig.ModelId ?? "(none)",
                    preferredModelId ?? "(none)",
                    llmConfig.Endpoint,
                    !string.IsNullOrWhiteSpace(llmConfig.KeyVaultId));
            }
            else
            {
                logger.LogWarning(
                    "[Chat] LlmConfig NOT resolved: agentId={AgentId} provider={ProviderId} model={ModelId} not found/disabled in file config",
                    agentId, preferredProviderId, normalizedModelId ?? "(none)");
            }
        }
        else
        {
            logger.LogInformation(
                "[Chat] agent={AgentId} has no PreferredProviderId; runtime will require explicit file config",
                agentId);
        }

        logger.LogInformation(
            "[Chat] Agent dispatch resolved: agentId={AgentId} templateId={TemplateId} hasCapability={HasCapability} allowShell={AllowShell}",
            agentId,
            agentTemplateId ?? "(none)",
            resolved.Policy is not null,
            resolved.Policy?.AllowShellExecution == true);

        return new ChatAgentDispatch(
            AgentId: agentId,
            DisplayName: displayName ?? agentId,
            AvatarUrl: avatarUrl,
            AgentTemplateId: agentTemplateId,
            PreferredProviderId: preferredProviderId,
            LlmConfig: llmConfig,
            CapabilityPolicy: resolved.Policy,
            ToolDefinitions: resolved.ToolDefinitions,
            SkillPackages: skillPackages);
    }

    private static Dictionary<string, string>? BuildChatIngressMetadata(
        AdminChatRequest req,
        ChatAgentDispatch dispatch,
        int fanoutIndex,
        int fanoutCount)
    {
        var metadata = new Dictionary<string, string>(
            req.Metadata ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(dispatch.AgentId))
        {
            metadata["agent_id"] = dispatch.AgentId;
            metadata["source_type"] = "agent";
            metadata["source_id"] = dispatch.AgentId;
            metadata["source_name"] = dispatch.DisplayName;
        }
        if (!string.IsNullOrWhiteSpace(dispatch.AvatarUrl))
            metadata["avatar_url"] = dispatch.AvatarUrl;
        if (!string.IsNullOrWhiteSpace(req.Audience))
            metadata["audience"] = req.Audience;
        if (req.TargetAgentIds is { Count: > 0 })
            metadata["target_agent_ids"] = string.Join(",", req.TargetAgentIds.Where(id => !string.IsNullOrWhiteSpace(id)));
        if (req.SuppressUserTranscript)
            metadata["suppress_user_transcript"] = "true";
        metadata["fanout_index"] = fanoutIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["fanout_count"] = fanoutCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return metadata.Count > 0 ? metadata : null;
    }

    private async Task<string?> ResolveInitialSessionTitleAsync(
        string workspaceId,
        AdminChatRequest req,
        ChatAgentDispatch dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.SessionId) && !req.ForceNewSession)
            return null;

        var agentTemplateId = string.IsNullOrWhiteSpace(dispatch.AgentTemplateId)
            ? dispatch.AgentId
            : dispatch.AgentTemplateId;

        return await sessionTitleService.BuildDefaultTitleAsync(
            workspaceId,
            agentTemplateId,
            dispatch.DisplayName,
            ct);
    }

    private static bool IsCameraVisualReasoningRequest(AdminChatRequest req)
    {
        return req.Metadata is not null
               && req.Metadata.TryGetValue("inputMode", out var inputMode)
               && string.Equals(inputMode, "camera", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveVisualProvider(
        IReadOnlyDictionary<string, string>? metadata,
        string? preferredProviderId)
    {
        if (metadata is not null
            && metadata.TryGetValue("visualProvider", out var metadataProvider)
            && !string.IsNullOrWhiteSpace(metadataProvider))
        {
            return metadataProvider.Trim();
        }

        return string.Equals(preferredProviderId, VisualReasoningProviders.DashScope, StringComparison.OrdinalIgnoreCase)
            ? VisualReasoningProviders.DashScope
            : VisualReasoningProviders.DashScope;
    }

    private static string ResolveVisualModel(
        IReadOnlyDictionary<string, string>? metadata,
        string? preferredModelId)
    {
        if (metadata is not null
            && metadata.TryGetValue("visualModel", out var metadataModel)
            && !string.IsNullOrWhiteSpace(metadataModel))
        {
            return metadataModel.Trim();
        }

        if (!string.IsNullOrWhiteSpace(preferredModelId))
        {
            var model = preferredModelId.Trim();
            if (model.Contains("vl", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("qvq", StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return "qwen3-vl-plus";
    }

    private static async Task RunSecondaryChatFanoutAsync(
        PlatformApiClient apiClient,
        ChatTranscriptWriter transcriptWriter,
        ISessionStateManager ssm,
        TokenUsageRecorder tokenUsageRecorder,
        ILogger<ChatApiController> logger,
        RuntimeTraceContext trace,
        string channelId,
        string userExternalId,
        string workspaceId,
        AdminChatRequest baseRequest,
        ChatAgentDispatch dispatch,
        string sessionId,
        int fanoutIndex,
        int fanoutCount,
        CancellationToken ct)
    {
        var request = baseRequest with
        {
            AgentId = dispatch.AgentId,
            SessionId = sessionId,
            SuppressUserTranscript = true,
            ForceNewSession = false,
        };
        var metadata = BuildChatIngressMetadata(request, dispatch, fanoutIndex, fanoutCount);
        var replyBuilder = new StringBuilder();
        var thinkingChunks = new List<TranscriptThinkingChunk>();
        string? latestUsageJson = null;
        string? streamMessageId = null;
        var assistantTranscriptPersisted = false;
        var framesWritten = 0;

        try
        {
            await foreach (var frame in apiClient.SendMessageStreamAsync(
                channelId: channelId,
                userExternalId: userExternalId,
                messageText: request.MessageText,
                workspaceId: workspaceId,
                sessionId: sessionId,
                llmConfig: dispatch.LlmConfig,
                agentTemplateId: dispatch.AgentTemplateId,
                agentInstanceId: dispatch.AgentId,
                capabilityPolicy: dispatch.CapabilityPolicy,
                toolDefinitions: dispatch.ToolDefinitions,
                skillPackages: dispatch.SkillPackages,
                forceNewSession: false,
                metadata: metadata,
                ct: ct))
            {
                if (frame.Event == "metadata")
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(frame.Data);
                        if (doc.RootElement.TryGetProperty("messageId", out var mid))
                            streamMessageId = mid.GetString();
                    }
                    catch
                    {
                        // Best-effort metadata extraction; the frame itself is still persisted.
                    }

                    var frameTrace = trace.WithSession(sessionId, workspaceId).WithAgent(dispatch.AgentId, dispatch.AgentTemplateId);
                    await ssm.AppendAsync(
                        sessionId,
                        workspaceId,
                        frame,
                        CancellationToken.None,
                        frameTrace,
                        RuntimeActivityComponents.AgentExecution,
                        $"chat.stream.fanout.{frame.Event}");
                    framesWritten++;
                }
                else if (frame.Event == "delta")
                {
                    var delta = TryReadStringProperty(frame.Data, "delta");
                    if (!string.IsNullOrEmpty(delta))
                        replyBuilder.Append(delta);
                }
                else if (frame.Event == "thinking")
                {
                    var delta = TryReadStringProperty(frame.Data, "delta");
                    if (!string.IsNullOrEmpty(delta))
                    {
                        thinkingChunks.Add(new TranscriptThinkingChunk(
                            delta,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    }
                }
                else if (frame.Event == "usage")
                {
                    latestUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                }
                else if (frame.Event == "done" && !string.IsNullOrEmpty(frame.Data))
                {
                    if (!assistantTranscriptPersisted)
                    {
                        var reply = TryReadStringProperty(frame.Data, "reply");
                        var assistantContent = !string.IsNullOrWhiteSpace(reply)
                            ? reply
                            : replyBuilder.ToString();
                        var doneUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                        var thinkingJson = thinkingChunks.Count > 0
                            ? JsonSerializer.Serialize(thinkingChunks, JsonOpts)
                            : null;

                        await transcriptWriter.PersistMessageAsync(
                            sessionId,
                            role: "agent",
                            content: assistantContent,
                            createdAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            thinkingJson,
                            doneUsageJson,
                            workspaceId: workspaceId,
                            agentInstanceId: dispatch.AgentId,
                            agentTemplateId: dispatch.AgentTemplateId,
                            ct: CancellationToken.None);
                        assistantTranscriptPersisted = true;
                    }

                    await RecordChatUsageAsync(
                        tokenUsageRecorder,
                        logger,
                        frame.Data,
                        sourceId: streamMessageId ?? $"{sessionId}:{dispatch.AgentId}:{fanoutIndex}",
                        workspaceId,
                        sessionId,
                        dispatch.PreferredProviderId,
                        dispatch.LlmConfig?.ModelId);
                }
            }

            logger.LogInformation(
                "[Chat:Fanout] Stream completed ws={Workspace} session={Session} agent={AgentId} idx={Index}/{Count} metadataFrames={Frames}",
                workspaceId, sessionId, dispatch.AgentId, fanoutIndex + 1, fanoutCount, framesWritten);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Chat:Fanout] Secondary stream failed ws={Workspace} session={Session} agent={AgentId}",
                workspaceId, sessionId, dispatch.AgentId);
        }
    }

    private static async Task RecordChatUsageAsync(
        TokenUsageRecorder tokenUsageRecorder,
        ILogger logger,
        string data,
        string sourceId,
        string workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("usage", out var usageEl)) return;

            var usageTokens = JsonSerializer.Deserialize<TokenUsageDto>(usageEl.GetRawText(), JsonOpts);
            if (usageTokens is null) return;
            PromptPrefixSnapshot? prefixSnapshot = null;
            if (root.TryGetProperty("prefixSnapshot", out var prefixEl)
                && prefixEl.ValueKind == JsonValueKind.Object)
            {
                prefixSnapshot = JsonSerializer.Deserialize<PromptPrefixSnapshot>(
                    prefixEl.GetRawText(),
                    JsonOpts);
            }

            await tokenUsageRecorder.RecordAsync(
                usageTokens,
                sourceType: "chat_message",
                sourceId: sourceId,
                workspaceId: workspaceId,
                sessionId: sessionId,
                providerId: providerId,
                modelId: modelId,
                prefixSnapshot: prefixSnapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Chat:Stats] Failed to record token usage via recorder");
        }
    }

    private static async Task<IReadOnlyList<WorkspaceAgentDto>> LoadWorkspaceAgentsForRoutingAsync(
        PlatformDbContext db,
        WorkspaceAgentFileService workspaceAgentFileService,
        int workspacePk,
        string workspaceId,
        CancellationToken ct)
    {
        var dbAgents = await db.WorkspaceAgents.AsNoTracking()
            .Where(a => a.WorkspaceEntityId == workspacePk)
            .Select(a => new WorkspaceAgentDto(
                a.AgentId,
                a.Name,
                a.Description,
                a.DisplayName,
                a.AvatarId,
                a.AvatarUrl,
                a.SourceTemplateId,
                null,
                null,
                null,
                null,
                a.IsEnabled,
                a.IsFrozen,
                a.CreatedAt,
                a.UpdatedAt))
            .ToListAsync(ct);

        var fileAgents = await workspaceAgentFileService.ListAgentsAsync(workspaceId, ct);
        return fileAgents
            .Concat(dbAgents)
            .GroupBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private sealed record ChatAgentDispatch(
        string AgentId,
        string DisplayName,
        string? AvatarUrl,
        string? AgentTemplateId,
        string? PreferredProviderId,
        LlmConfig? LlmConfig,
        CapabilityPolicy? CapabilityPolicy,
        IReadOnlyList<LlmToolDefinition>? ToolDefinitions,
        IReadOnlyList<SkillPackageInfo>? SkillPackages);

    private sealed record TranscriptThinkingChunk(string Text, long Timestamp);

    private static string? TryReadStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadUsageJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                return usage.GetRawText();

            return LooksLikeUsagePayload(root)
                ? root.GetRawText()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeUsagePayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        return root.TryGetProperty("promptTokens", out _)
            || root.TryGetProperty("PromptTokens", out _)
            || root.TryGetProperty("completionTokens", out _)
            || root.TryGetProperty("CompletionTokens", out _)
            || root.TryGetProperty("totalTokens", out _)
            || root.TryGetProperty("TotalTokens", out _);
    }

    private sealed record ResolvedCapabilities(
        CapabilityPolicy? Policy,
        IReadOnlyList<LlmToolDefinition>? ToolDefinitions,
        string Source,
        int CapabilityCount);

    /// <summary>
    /// 规范化模板 ID：前端会给全局模板附加 "global:" 前缀以区分工作区模板，
    /// 后端查询时需剥除前缀后再匹配数据库中的 TemplateId。
    /// </summary>
    private static (string RawId, string GlobalId, bool IsExplicitGlobal) NormalizeTemplateId(string templateId)
    {
        const string prefix = "global:";
        return templateId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? (templateId, templateId[prefix.Length..], true)
            : (templateId, templateId, false);
    }

    private static async Task<GlobalAgentTemplateDto?> ResolveAgentTemplateAsync(
        AgentTemplateFileService templateFileService,
        string templateId,
        CancellationToken ct)
    {
        var (_, globalId, _) = NormalizeTemplateId(templateId);
        return await templateFileService.GetTemplateAsync(globalId, ct);
    }

    private static async Task<ResolvedCapabilities> ResolveCapabilitiesAsync(
        PlatformDbContext db,
        AgentTemplateFileService templateFileService,
        IPuddingToolCatalogService toolCatalog,
        IToolPermissionPolicyService toolPermissionPolicy,
        string workspaceId,
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return new ResolvedCapabilities(null, null, "none", 0);

        var (rawId, globalId, isExplicitGlobal) = NormalizeTemplateId(templateId);

        // 工作区模板：已迁移到文件管理，通过 AgentTemplateFileService 统一读取
        // 不再从 DB WorkspaceAgentTemplates 表查询

        // 全局模板：ADR-044 后以文件模板為主源。
        var globalTemplate = await templateFileService.GetTemplateAsync(globalId, ct);
        if (globalTemplate is not null)
        {
            var selected = globalTemplate.SelectedCapabilityIds;
            var selectedToolNames = ResolveSelectedToolNames(selected, toolCatalog);
            var selectedDescriptors = ResolveSelectedToolDescriptors(selected, toolCatalog);
            var allowedToolNamesJson = (globalTemplate.AllowedToolNames is { Count: > 0 } names)
                ? JsonSerializer.Serialize(names)
                : "[]";
            return new ResolvedCapabilities(
                BuildPolicy(
                globalTemplate.AllowFileWrite,
                globalTemplate.AllowShellExecution,
                globalTemplate.AllowNetworkAccess,
                allowedToolNamesJson,
                globalTemplate.Role,
                selectedToolNames,
                toolCatalog,
                toolPermissionPolicy),
                BuildToolDefinitions(selectedDescriptors),
                "global-file-template",
                selectedToolNames.Count);
        }

        // DB 不存在模板时，按常见 code-agent 兜底（同时兼容 global: 前缀写法）
        if (globalId.Equals("code-agent", StringComparison.OrdinalIgnoreCase)
            || globalId.Equals("workspace-task-agent", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedCapabilities(
                new CapabilityPolicy
                {
                    AllowFileWrite = true,
                    AllowShellExecution = true,
                    AllowNetworkAccess = false,
                    AllowedToolNames = ["shell", "file_read", "file_write", "file_patch"],
                    DefaultToolNames = ["file_read", "search_memory", "grep_memory",
                        "query_sessions", "http_fetch", "file_search", "search_grep",
                        "spawn_sub_agent", "manage_tasks"],
                    RequiresGrantToolNames = ["file_patch", "file_write", "shell"],
                },
                [
                    new LlmToolDefinition
                    {
                        Name = "shell",
                        Description = "Execute a host shell command",
                        Parameters = new ToolParameterSchema(
                            [
                                new ToolParameter("command", "string", "Command to execute on the host"),
                                new ToolParameter("shell", "string", "Shell mode: auto, wsl, bash, cmd, or powershell. Default: auto"),
                                new ToolParameter("working_directory", "string", "Host working directory. Default: current runtime directory"),
                                new ToolParameter("timeout_seconds", "integer", "Timeout in seconds, 1-600. Default: 30"),
                            ],
                            ["command"]),
                    }
                ],
                "fallback-code-agent",
                0);
        }

        return new ResolvedCapabilities(null, null, "none", 0);
    }

    private static IReadOnlyList<LlmToolDefinition> BuildToolDefinitions(
        IReadOnlyList<ToolDescriptor> descriptors)
    {
        var map = new Dictionary<string, LlmToolDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in descriptors)
        {
            if (map.ContainsKey(descriptor.ToolId))
                continue;

            map[descriptor.ToolId] = new LlmToolDefinition
            {
                Name = descriptor.ToolId,
                Description = descriptor.Description,
                Parameters = descriptor.Parameters,
            };
        }

        return map.Values.ToList();
    }

    private static readonly string[] TerminalLifecycleToolIds =
    [
        "terminal_start",
        "terminal_wait",
        "terminal_read",
        "terminal_status",
        "terminal_cancel",
        "terminal_input",
    ];

    private static bool IsTerminalExecuteAlias(string value)
        => value.Equals("terminal_execute", StringComparison.OrdinalIgnoreCase)
        || value.Equals("cap-terminal-execute", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ToolDescriptor> ResolveSelectedToolDescriptors(
        IEnumerable<string> selectedCapabilityOrToolIds,
        IPuddingToolCatalogService toolCatalog)
    {
        var descriptors = toolCatalog.ListTools();
        var byToolId = descriptors.ToDictionary(d => d.ToolId, StringComparer.OrdinalIgnoreCase);
        var byCapabilityId = descriptors.ToDictionary(d => ToolIdToCapabilityId(d.ToolId), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var selected in selectedCapabilityOrToolIds)
        {
            if (string.IsNullOrWhiteSpace(selected))
                continue;

            var id = selected.Trim();
            if (IsTerminalExecuteAlias(id))
            {
                foreach (var terminalToolId in TerminalLifecycleToolIds)
                {
                    if (byToolId.TryGetValue(terminalToolId, out var descriptor))
                        result.TryAdd(descriptor.ToolId, descriptor);
                }
                continue;
            }

            if (byCapabilityId.TryGetValue(id, out var byCap))
            {
                result.TryAdd(byCap.ToolId, byCap);
                continue;
            }

            if (byToolId.TryGetValue(id, out var byTool))
                result.TryAdd(byTool.ToolId, byTool);
        }

        return result.Values.ToList();
    }

    private static IReadOnlyList<string> ResolveSelectedToolNames(
        IEnumerable<string> selectedCapabilityOrToolIds,
        IPuddingToolCatalogService toolCatalog)
        => ResolveSelectedToolDescriptors(selectedCapabilityOrToolIds, toolCatalog)
            .Select(d => d.ToolId)
            .ToList();

    private static string ToolIdToCapabilityId(string toolId)
        => $"cap-{toolId.Trim().Replace('_', '-').ToLowerInvariant()}";

    private static string SummarizeToolDefinitions(IReadOnlyList<LlmToolDefinition>? tools)
        => tools is { Count: > 0 }
            ? string.Join(",", tools.Select(t => t.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            : "";

    private static CapabilityPolicy BuildPolicy(
        bool allowFileWrite,
        bool allowShellExecution,
        bool allowNetworkAccess,
        string allowedToolNamesJson,
        string role,
        IReadOnlyList<string>? selectedToolNames = null,
        IPuddingToolCatalogService? toolCatalog = null,
        IToolPermissionPolicyService? toolPermissionPolicy = null)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = toolCatalog?.ListTools() ?? [];
        var descriptorByTool = descriptors.ToDictionary(d => d.ToolId, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in JsonSerializer.Deserialize<List<string>>(allowedToolNamesJson) ?? [])
            {
                if (string.IsNullOrWhiteSpace(t))
                    continue;

                var trimmed = t.Trim();
                if (IsTerminalExecuteAlias(trimmed))
                {
                    foreach (var terminalToolId in TerminalLifecycleToolIds)
                        tools.Add(terminalToolId);
                }
                else
                {
                    tools.Add(trimmed);
                }
            }
        }
        catch
        {
            // ignore malformed JSON and continue with selected capabilities.
        }

        foreach (var toolName in selectedToolNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(toolName))
                tools.Add(toolName.Trim());
        }

        var isTaskRole = role.Equals("Task", StringComparison.OrdinalIgnoreCase);

        if (isTaskRole && tools.Count == 0)
        {
            tools.UnionWith([
                "terminal_start",
                "terminal_wait",
                "terminal_read",
                "terminal_status",
                "terminal_cancel",
                "terminal_input",
                "shell",
                "file_read",
                "list_dir",
                "file_write",
                "file_patch",
                "apply_patch",
            ]);
        }

        if (toolPermissionPolicy is null)
            throw new InvalidOperationException("Tool permission policy service is required.");

        var policy = toolPermissionPolicy.BuildCapabilityPolicy(
            descriptors,
            tools.Where(descriptorByTool.ContainsKey),
            isTaskRole);

        return policy with
        {
            AllowFileWrite = allowFileWrite || policy.AllowFileWrite || isTaskRole,
            AllowShellExecution = allowShellExecution || policy.AllowShellExecution || isTaskRole,
            AllowNetworkAccess = allowNetworkAccess || policy.AllowNetworkAccess,
        };
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    /// <summary>
    /// 规范化 Provider 模型 ID。mimo 网关模型名大小写敏感，历史配置中的 "MiMo-V2.5" 需降为 "mimo-v2.5"。
    /// </summary>
    private static string? NormalizePreferredModelId(string providerId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return modelId;

        var trimmed = modelId.Trim();
        if (providerId.Equals("mimo", StringComparison.OrdinalIgnoreCase))
            return trimmed.ToLowerInvariant();

        return trimmed;
    }

    /// <summary>解析 Agent 模板关联的 Skill 包列表，生成 MinIO 预签名下载 URL。</summary>
    private static async Task<IReadOnlyList<SkillPackageInfo>?> ResolveSkillPackagesAsync(
        PlatformDbContext db,
        MinioStorageService minio,
        AgentTemplateFileService templateFileService,
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (_, globalId, _) = NormalizeTemplateId(templateId);

        // 全局模板 Skill 选择以文件模板为主源。
        var globalTemplate = await templateFileService.GetTemplateAsync(globalId, ct);
        if (globalTemplate is null)
            return null;

        var selectedIds = globalTemplate.SelectedSkillPackageIds;
        if (selectedIds.Count == 0)
            return null;

        var packages = await db.SkillPackages.AsNoTracking()
            .Where(s => selectedIds.Contains(s.SkillPackageId) && s.IsEnabled)
            .ToListAsync(ct);

        if (packages.Count == 0)
            return null;

        var result = new List<SkillPackageInfo>(packages.Count);
        foreach (var pkg in packages)
        {
            var url = await minio.GetPresignedDownloadUrlAsync(pkg.ObjectKey, 86400, ct);
            result.Add(new SkillPackageInfo
            {
                SkillPackageId = pkg.SkillPackageId,
                Name           = pkg.Name,
                Description    = pkg.Description,
                Version        = pkg.Version,
                DownloadUrl    = url,
            });
        }
        return result;
    }


    /// <summary>
    /// 从模板（工作区优先，全局兜底）解析 ReasoningEffort。
    /// </summary>
    private static async Task<string?> ResolveReasoningEffortAsync(
        PlatformDbContext db,
        AgentTemplateFileService templateFileService,
        string workspaceId,
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (rawId, globalId, isExplicitGlobal) = NormalizeTemplateId(templateId);

        // 工作区模板 ReasoningEffort 已迁移到文件管理，统一从文件模板读取
        var globalTemplate = await templateFileService.GetTemplateAsync(globalId, ct);
        return globalTemplate?.ReasoningEffort;
    }
}
