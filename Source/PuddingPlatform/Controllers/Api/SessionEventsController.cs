using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Diagnostics;
using System.Diagnostics;
using System.Text.Json;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 会话事件 API — 历史加载（REST）与实时订阅（SSE）。
/// 
/// 替代旧 ChatApiController 的临时 Channel 模式。
/// 关联 ADR：Docs/07架构/16会话状态层与客户端解耦ADR.md §5
/// </summary>
[Authorize]
[ApiController]
[Route("api/sessions")]
public class SessionEventsController : ControllerBase
{

    private readonly ISessionStateManager _ssm;
    private readonly ISessionEventStream _eventStream;
    private readonly ISessionProjectionStore _projectionStore;
    private readonly IContextCompactionService _compactionService;
    private readonly CacheDiagnosticsService _cacheDiagnosticsService;
    private readonly ISessionTimelineRecorder _timelineRecorder;
    private readonly PlatformApiClient _platformApi;
    private readonly PlatformDbContext _db;
    private readonly IAgentRuntimeProfileResolver _agentRuntimeProfileResolver;
    private readonly ILlmConfigService _llmConfigService;
    private readonly IRawSessionLogService _rawLogs;
    private readonly TokenCostService _tokenCostService;
    private readonly IConversationEventStore _conversationEventStore;
    private readonly IRequestCompactionHandler _requestCompactionHandler;
    private readonly ILogger<SessionEventsController> _logger;

    public SessionEventsController(
        ISessionStateManager ssm,
        ISessionEventStream eventStream,
        ISessionProjectionStore projectionStore,
        IContextCompactionService compactionService,
        CacheDiagnosticsService cacheDiagnosticsService,
        ISessionTimelineRecorder timelineRecorder,
        PlatformApiClient platformApi,
        PlatformDbContext db,
        IAgentRuntimeProfileResolver agentRuntimeProfileResolver,
        ILlmConfigService llmConfigService,
        IRawSessionLogService rawLogs,
        TokenCostService tokenCostService,
        IConversationEventStore conversationEventStore,
        IRequestCompactionHandler requestCompactionHandler,
        ILogger<SessionEventsController> logger)
    {
        _ssm = ssm;
        _eventStream = eventStream;
        _projectionStore = projectionStore;
        _compactionService = compactionService;
        _cacheDiagnosticsService = cacheDiagnosticsService;
        _timelineRecorder = timelineRecorder;
        _platformApi = platformApi;
        _db = db;
        _agentRuntimeProfileResolver = agentRuntimeProfileResolver;
        _llmConfigService = llmConfigService;
        _rawLogs = rawLogs;
        _tokenCostService = tokenCostService;
        _conversationEventStore = conversationEventStore;
        _requestCompactionHandler = requestCompactionHandler;
        _logger = logger;
    }

    /// <summary>
    /// 获取会话事件历史（分页/游标加载）。
    /// GET /api/sessions/{sessionId}/events?from={seq}&limit={N}
    /// ADR-057: 统一从 conversation_events 读取。
    /// </summary>
    [HttpGet("{sessionId}/events")]
    public async Task<ActionResult> GetEvents(
        string sessionId,
        [FromQuery] long? from,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > 200)
            return BadRequest(new { message = "limit 必须在 1-200 之间" });

        var page = await _conversationEventStore.ReadForwardAsync(
            sessionId, afterExclusive: from ?? 0, throughInclusive: null, limit, ct);

        // The replay endpoint uses the same canonical envelope as SSE. Keeping
        // turn/message identity outside the payload is required for deterministic
        // frontend projection after reconnect.
        var events = page.Events.Select(e => new
        {
            eventId = e.EventId,
            conversationId = e.ConversationId,
            sequence = e.Sequence,
            type = e.Type,
            schemaVersion = e.SchemaVersion,
            commandId = e.CommandId,
            turnId = e.TurnId,
            runId = e.RunId,
            messageId = e.MessageId,
            occurredAt = e.OccurredAt,
            payload = e.Payload,
        }).ToList();

        var minSeq = events is { Count: > 0 } ? events[0].sequence : 0L;
        var maxSeq = events is { Count: > 0 } ? events[^1].sequence : 0L;

        _logger.LogDebug(
            "[SessionEvents] GET history session={Session} from={From} limit={Limit} count={Count} hasMore={HasMore}",
            sessionId, from, limit, page.Events.Count, page.HasMore);

        return Ok(new
        {
            events,
            hasMore = page.HasMore,
            minSequence = minSeq,
            maxSequence = maxSeq,
            count = events.Count,
        });
    }

    /// <summary>
    /// 按消息ID查询单条消息（ChatMessages.Id）。
    /// GET /api/sessions/{sessionId}/messages/by-id/{messageId}
    /// </summary>
    [HttpGet("{sessionId}/messages/by-id/{messageId:long}")]
    public async Task<ActionResult> GetMessageById(
        string sessionId,
        long messageId,
        CancellationToken ct = default)
    {
        // 从 sessionId 推断 workspaceId：查 session_event_log
        var workspaceId = await _db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .Select(e => e.WorkspaceId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(workspaceId))
            return NotFound(new { message = "Session not found", sessionId });

        var msg = await _rawLogs.GetMessageByIdAsync(workspaceId, messageId, ct);
        if (msg is null)
            return NotFound(new { message = "Message not found", messageId });

        return Ok(new
        {
            msg.MessageId,
            msg.SessionId,
            msg.WorkspaceId,
            msg.Role,
            msg.Content,
            msg.CreatedAt,
            msg.EvidenceRef,
        });
    }

    /// <summary>
    /// 重放会话事件 — 从指定序列号开始完整重建会话状态。
    /// GET /api/sessions/{sessionId}/replay?from={seq}&limit={N}
    /// 包含事件列表、当前会话状态和子代理列表。
    /// 关联 ADR：Docs/07架构/20会话状态机与事件规范ADR.md §5
    /// </summary>
    [HttpGet("{sessionId}/replay")]
    public async Task<ActionResult<SessionReplayResult>> Replay(
        string sessionId,
        [FromQuery] long? from,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > 500)
            return BadRequest(new { message = "limit 必须在 1-500 之间" });

        // P0: 验证 session 存在性，防止对已删除的幻影 session 进行 replay
        //    规则：Platform API 有记录，或本地 DB 有历史事件/消息/子代理证据
        if (!await SessionExistsAsync(sessionId, ct))
        {
            _logger.LogWarning(
                "[SessionEvents] Replay denied: session not found session={Session}",
                sessionId);
            return NotFound(new { message = "Session not found", sessionId });
        }

        var result = await _ssm.ReplaySessionAsync(sessionId, from, limit, ct);

        _logger.LogDebug(
            "[SessionEvents] GET replay session={Session} from={From} limit={Limit} events={EventCount} total={Total} hasMore={HasMore}",
            sessionId, from, limit, result.Events.Count, result.TotalEventCount, result.HasMore);

        return Ok(result);
    }

    /// <summary>
    /// 实时订阅会话事件（SSE）。
    /// GET /api/sessions/{sessionId}/events/stream?afterSequence={seq}
    /// 断线重连时使用 Last-Event-ID header 或 afterSequence 游标补发缺口事件。
    /// </summary>
    [HttpGet("{sessionId}/events/stream")]
    public async Task EventsStream(
        string sessionId,
        [FromQuery] long? afterSequence = null,
        CancellationToken ct = default)
    {
        if (!afterSequence.HasValue)
        {
            var lastEventId = Request.Headers["Last-Event-ID"].FirstOrDefault();
            if (lastEventId is not null && long.TryParse(lastEventId, out var parsed))
                afterSequence = parsed;
        }
        // P0: 验证 session 存在性（必须在设置 SSE header 之前执行）
        //    规则：Platform API 有记录，或本地 DB 有历史事件/消息/子代理证据
        var sessionStatus = await GetSessionStatusAsync(sessionId, ct);
        if (sessionStatus is null)
        {
            _logger.LogWarning(
                "[SessionEvents] SSE denied: session not found session={Session}",
                sessionId);
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // P0: Frozen session 不允许 SSE 订阅
        if (sessionStatus == SessionStatus.Frozen)
        {
            _logger.LogWarning(
                "[SessionEvents] SSE denied: session is frozen session={Session}",
                sessionId);
            Response.StatusCode = StatusCodes.Status410Gone;
            Response.ContentType = "application/json";
            await Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    code = "conversation_frozen",
                    message = "This conversation is frozen and no longer accepts events.",
                }),
                ct);
            return;
        }

        // ADR-057: snapshot_required — cursor below minimum available sequence.
        var cursor = afterSequence ?? 0L;
        if (cursor > 0)
        {
            var bounds = await _conversationEventStore.GetBoundsAsync(sessionId, ct);
            if (bounds.MinSequence.HasValue && cursor < bounds.MinSequence.Value)
            {
                _logger.LogWarning(
                    "[SessionEvents] SSE snapshot_required session={Session} cursor={Cursor} min={Min}",
                    sessionId, cursor, bounds.MinSequence.Value);
                Response.StatusCode = StatusCodes.Status410Gone;
                Response.ContentType = "application/json";
                await Response.WriteAsync(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        code = "snapshot_required",
                        minimumAvailableSequence = bounds.MinSequence.Value,
                        snapshotUrl = $"/api/conversations/{sessionId}/bootstrap",
                    }),
                    ct);
                return;
            }
        }

        ConfigureSseResponse(Response);

        _logger.LogInformation(
            "[SessionEvents] SSE subscribed session={Session}", sessionId);
        await RecordSseTimelineAsync(
            _timelineRecorder,
            sessionId,
            "sse.client.connected",
            "sse.subscribe",
            RuntimeActivityStatuses.Started,
            metadata: null,
            logger: _logger,
            ct: ct);

        // ADR-056: ISessionEventStream.FollowAsync handles replay + live + dedup automatically.
        try
        {
            var after = afterSequence ?? 0L;
            await foreach (var envelope in _eventStream.FollowAsync(sessionId, after, ct))
            {
                // Heartbeat: send as SSE comment, don't set id.
                if (envelope.EventType == "heartbeat")
                {
                    await Response.WriteAsync(": heartbeat\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    continue;
                }

                await WriteEnvelopeAsSseAsync(Response, envelope, sessionId, _logger, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "[SessionEvents] SSE disconnected session={Session}", sessionId);
        }
        catch (IOException)
        {
            _logger.LogDebug(
                "[SessionEvents] SSE write failed (client disconnected) session={Session}", sessionId);
        }

        await RecordSseTimelineAsync(
            _timelineRecorder,
            sessionId,
            "sse.session.ended",
            "sse.subscribe",
            RuntimeActivityStatuses.Succeeded,
            metadata: null,
            logger: _logger,
            ct: CancellationToken.None);
    }

    /// <summary>
    /// P0: Bootstrap — 初始化加载 conversation 快照、turns、messages、投影游标。
    /// GET /api/conversations/{id}/bootstrap
    /// <para>
    /// 前端流程：加载 bootstrap → 用 snapshotCursor 建立本地状态 → SSE 从 snapshotCursor+1 开始。
    /// P1: 增加投影追平等待——投影落后时等待 projectTimeoutMs 后返回可用 checkpoint。
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/api/conversations/{conversationId}/bootstrap")]
    public async Task<ActionResult> GetConversationBootstrap(
        string conversationId,
        [FromQuery] int messageLimit = 50,
        [FromQuery] int projectTimeoutMs = 500,
        CancellationToken ct = default)
    {
        // Read projected cursor and actual event head from conversation_events
        var projectedCursor = await _projectionStore.GetProjectedCursorAsync(conversationId, ct);
        var eventHead = 0L;
        try
        {
            var bounds = await _conversationEventStore.GetBoundsAsync(conversationId, ct);
            eventHead = bounds.MaxSequence ?? 0;
        }
        catch (Exception) { /* ignore — possible for brand-new sessions */ }

        // If projection is behind, wait for it to catch up (up to projectTimeoutMs)
        if (projectedCursor < eventHead && eventHead > 0)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(projectTimeoutMs);
            while (DateTimeOffset.UtcNow < deadline && projectedCursor < eventHead)
            {
                await Task.Delay(50, ct);
                projectedCursor = await _projectionStore.GetProjectedCursorAsync(conversationId, ct);
            }
            _logger.LogInformation(
                "[Bootstrap] Projection wait session={Session} projected={Projected} head={Head} remaining={Gap}",
                conversationId, projectedCursor, eventHead, eventHead - projectedCursor);
        }

        // ADR-059: snapshotCursor = projection checkpoint (not event head).
        // If projection hasn't caught up, SSE will replay the gap from checkpoint+1.
        var snapshotCursor = Math.Min(eventHead, projectedCursor);

        // Load recent messages from projected ChatMessages table
        var messages = await _db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(messageLimit)
            .Select(m => new
            {
                id = m.Id,
                role = m.Role,
                content = m.Content,
                createdAt = m.CreatedAt,
            })
            .ToListAsync(ct);

        messages.Reverse();

        var hasMoreHistory = messages.Count >= messageLimit;

        // A snapshot cursor may only cover state that is present in this response.
        // Therefore include terminal Turns as well as active Turns; otherwise a
        // turn.failed before snapshotCursor can never be reconstructed by replay.
        var projectedTurns = new List<object>();
        var lifecycleEvents = new List<object>();
        var subAgentEvents = new List<object>();
        try
        {
            var recentEvents = await _conversationEventStore.ReadBackwardAsync(
                conversationId, long.MaxValue, limit: 500, ct);
            lifecycleEvents.AddRange(
                recentEvents.Events
                    .Where(e => e.Type is
                        ConversationEventTypes.ContextCompactionStarted or
                        ConversationEventTypes.ContextCompactionCompleted or
                        ConversationEventTypes.ContextCompactionFailed)
                    .OrderBy(e => e.Sequence)
                    .Select(e => (object)new
                    {
                        eventId = e.EventId,
                        conversationId = e.ConversationId,
                        sequence = e.Sequence,
                        type = e.Type,
                        schemaVersion = e.SchemaVersion,
                        commandId = e.CommandId,
                        turnId = e.TurnId,
                        runId = e.RunId,
                        messageId = e.MessageId,
                        occurredAt = e.OccurredAt,
                        payload = e.Payload,
                    }));
            var turnGroups = recentEvents.Events
                .Where(e => !string.IsNullOrWhiteSpace(e.TurnId))
                .GroupBy(e => e.TurnId!, StringComparer.Ordinal)
                .ToList();
            var turnIds = turnGroups.Select(group => group.Key).ToList();
            var commandIdentities = await _db.ChatExecutionCommands
                .AsNoTracking()
                .Where(command =>
                    command.SessionId == conversationId
                    && turnIds.Contains(command.TurnId))
                .Select(command => new
                {
                    command.TurnId,
                    command.UserMessageId,
                    AssistantMessageId = command.MessageId,
                })
                .ToDictionaryAsync(command => command.TurnId, ct);

            foreach (var group in turnGroups)
            {
                var ordered = group.OrderBy(e => e.Sequence).ToList();
                var accepted = ordered.FirstOrDefault(
                    e => e.Type == ConversationEventTypes.TurnAccepted);
                var terminal = ordered.LastOrDefault(e =>
                    e.Type is
                        ConversationEventTypes.TurnCompleted or
                        ConversationEventTypes.TurnFailed or
                        ConversationEventTypes.TurnCancelled);
                commandIdentities.TryGetValue(group.Key, out var identity);

                var status = terminal?.Type switch
                {
                    ConversationEventTypes.TurnCompleted => "completed",
                    ConversationEventTypes.TurnFailed => "failed",
                    ConversationEventTypes.TurnCancelled => "cancelled",
                    _ => "active",
                };

                projectedTurns.Add(new
                {
                    turnId = group.Key,
                    status,
                    userMessageId = identity?.UserMessageId
                        ?? ReadPayloadString(accepted?.Payload, "userMessageId"),
                    assistantMessageId = identity?.AssistantMessageId
                        ?? terminal?.MessageId,
                    createdAt = accepted?.OccurredAt.ToUnixTimeMilliseconds()
                        ?? ordered[0].OccurredAt.ToUnixTimeMilliseconds(),
                    terminalSequence = terminal?.Sequence,
                    errorCode = ReadPayloadString(terminal?.Payload, "errorCode"),
                    errorMessage = ReadPayloadString(terminal?.Payload, "errorMessage"),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Bootstrap] Failed to read turn snapshot conv={ConvId}", conversationId);
        }

        try
        {
            // A 150-round child can produce hundreds of internal events. Query
            // by type prefix so message deltas cannot evict the run snapshot.
            var recentSubAgentEvents =
                await _conversationEventStore.ReadByTypePrefixBackwardAsync(
                    conversationId,
                    "subagent.",
                    long.MaxValue,
                    limit: 5000,
                    ct);
            subAgentEvents.AddRange(
                recentSubAgentEvents.Events.Select(e => (object)new
                {
                    eventId = e.EventId,
                    conversationId = e.ConversationId,
                    sequence = e.Sequence,
                    type = e.Type,
                    schemaVersion = e.SchemaVersion,
                    commandId = e.CommandId,
                    turnId = e.TurnId,
                    runId = e.RunId,
                    messageId = e.MessageId,
                    occurredAt = e.OccurredAt,
                    payload = e.Payload,
                }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[Bootstrap] Failed to read sub-agent snapshot conv={ConvId}",
                conversationId);
        }

        return Ok(new
        {
            conversation = new { conversationId, sessionId = conversationId },
            turns = projectedTurns.ToArray(),
            messages,
            lifecycleEvents = lifecycleEvents.ToArray(),
            subAgentEvents = subAgentEvents.ToArray(),
            snapshotCursor,
            hasMoreHistory,
            historyCursor = (long?)null,
        });
    }

    private static string? ReadPayloadString(JsonElement? payload, string propertyName)
        => payload is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>
    /// P2: 获取流指标。
    /// GET /api/sessions/metrics
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/api/sessions/metrics")]
    public ActionResult GetStreamMetrics()
    {
        var metrics = HttpContext.RequestServices.GetService<StreamMetrics>();
        if (metrics is null)
            return Ok(new { note = "StreamMetrics not registered" });

        return Ok(metrics.Snapshot());
    }

    /// <summary>
    /// 获取会话的投影游标。
    /// GET /api/sessions/{sessionId}/projected-cursor
    /// <para>
    /// ADR-056: 浏览器加载历史消息 → ChatMessages；然后从 projectedThroughSequence 之后读取尾部队列事件。
    /// ChatMessages 是 SessionEventLog 的物化投影，不是独立事实源。
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{sessionId}/projected-cursor")]
    public async Task<ActionResult> GetProjectedCursor(string sessionId, CancellationToken ct)
    {
        var cursor = await _projectionStore.GetProjectedCursorAsync(sessionId, ct);
        return Ok(new { sessionId, projectedThroughSequence = cursor });
    }

    private static async Task WriteEnvelopeAsSseAsync(
        HttpResponse response,
        SessionEventEnvelope envelope,
        string sessionId,
        ILogger logger,
        CancellationToken ct)
    {
        // ADR-057 canonical SSE format:
        // id: {sequence}
        // event: {canonicalType}
        // data: {full ConversationEvent envelope as JSON}
        var envelopeJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            eventId = envelope.EventId,
            conversationId = envelope.ConversationId ?? envelope.SessionId,
            sequence = envelope.Sequence,
            type = envelope.EventType,
            schemaVersion = envelope.SchemaVersion,
            commandId = envelope.CommandId,
            turnId = envelope.TurnId,
            runId = envelope.RunId,
            messageId = envelope.MessageId,
            occurredAt = envelope.OccurredAt,
            payload = envelope.Payload,
        });

        var sb = new System.Text.StringBuilder(64 + envelopeJson.Length);

        sb.Append("id: ").Append(envelope.Sequence).Append('\n');
        sb.Append("event: ").Append(envelope.EventType).Append('\n');
        sb.Append("data: ").Append(envelopeJson).Append('\n');
        sb.Append('\n');

        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// 获取会话中子代理状态列表。
    /// GET /api/sessions/{sessionId}/sub-agents
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{sessionId}/sub-agents")]
    public async Task<ActionResult<IReadOnlyList<SubAgentStatus>>> GetSubAgents(
        string sessionId, CancellationToken ct)
    {
        var agents = await _ssm.GetSubAgentsAsync(sessionId, ct);
        return Ok(agents);
    }

    /// <summary>
    /// 获取会话当前运行时状态。
    /// GET /api/sessions/{sessionId}/state
    /// </summary>
    [HttpGet("{sessionId}/state")]
    public async Task<ActionResult<object>> GetState(
        string sessionId, CancellationToken ct)
    {
        var state = await _ssm.GetSessionStateAsync(sessionId, ct);
        var runningSubAgents = await _ssm.GetRunningSubAgentCountAsync(sessionId, ct);
        return Ok(new
        {
            sessionId,
            state = state.ToString(),
            runningSubAgents,
        });
    }

    /// <summary>
    /// 获取当前会话上下文健康状态。
    /// GET /api/sessions/{sessionId}/context-health
    /// </summary>
    [HttpGet("{sessionId}/context-health")]
    public async Task<ActionResult<ContextHealthSnapshot>> GetContextHealth(
        string sessionId,
        CancellationToken ct)
    {
        var capacity = await ResolveContextCapacityAsync(sessionId, ct);
        if (capacity is null)
        {
            _logger.LogWarning(
                "[SessionEvents] Context health failed: context window unresolved session={SessionId}",
                sessionId);
            return Conflict(new
            {
                code = "context_window_unresolved",
                message = "无法解析当前 Session 的上下文窗口。请检查最近一次 LLM usage 或 Agent 模板的 LLM 模型配置。"
            });
        }

        var health = await _compactionService.GetHealthAsync(
            sessionId,
            ct,
            contextWindowTokens: capacity.ContextWindowTokens,
            maxOutputTokens: capacity.MaxOutputTokens);
        return Ok(health);
    }

    private async Task<ResolvedContextCapacity?> ResolveContextCapacityAsync(
        string sessionId,
        CancellationToken ct)
    {
        var latestMessage = await _db.ChatMessages.AsNoTracking()
            .Where(message => message.SessionId == sessionId)
            .OrderByDescending(message => message.CreatedAt)
            .Select(message => new ContextAgentBindingRow(
                message.WorkspaceId,
                message.AgentInstanceId))
            .FirstOrDefaultAsync(ct);

        var session = await _platformApi.GetSessionAsync(sessionId, ct);
        var workspaceId = FirstNonBlank(session?.WorkspaceId, latestMessage?.WorkspaceId);
        var agentId = FirstNonBlank(session?.AgentInstanceId, session?.PrincipalId, latestMessage?.AgentInstanceId);
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId))
            return null;

        AgentRuntimeProfile profile;
        try
        {
            // Context health is a read-only status endpoint. It must reuse the
            // runtime profile boundary first, then ask the LLM configuration
            // service for the provider/model limits. Usage records are evidence
            // of previous calls, not the source of model capacity.
            profile = await _agentRuntimeProfileResolver.ResolveAsync(workspaceId, agentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[SessionEvents] Context health failed: profile unresolved session={SessionId} workspace={WorkspaceId} agent={AgentId}",
                sessionId,
                workspaceId,
                agentId);
            return null;
        }

        var providerId = profile.PreferredProviderId;
        var modelId = profile.PreferredModelId ?? profile.LlmConfig?.ModelId;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return null;

        if (profile.LlmConfig?.MaxContextTokens is > 0)
        {
            return new ResolvedContextCapacity(
                profile.LlmConfig.MaxContextTokens.Value,
                profile.LlmConfig.MaxOutputTokens is > 0 ? profile.LlmConfig.MaxOutputTokens : null);
        }

        var model = _llmConfigService.GetAllModels().FirstOrDefault(item =>
            string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

        return model?.MaxContextTokens > 0
            ? new ResolvedContextCapacity(model.MaxContextTokens, model.MaxOutputTokens > 0 ? model.MaxOutputTokens : null)
            : null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>
    /// 主动压缩当前会话上下文。
    /// POST /api/sessions/{sessionId}/compact
    /// </summary>
    [HttpPost("{sessionId}/compact")]
    public async Task<ActionResult<CompactSessionResponse>> Compact(
        string sessionId,
        [FromBody] CompactSessionRequest request,
        CancellationToken ct)
    {
        var workspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId)
            ? "default"
            : request.WorkspaceId;
        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            return BadRequest(new
            {
                errorCode = "agent_id_required",
                message = "Compaction requires the current Agent instance ID.",
            });
        }

        var compactionId = string.IsNullOrWhiteSpace(request.CompactionId)
            ? Guid.NewGuid().ToString("N")
            : request.CompactionId.Trim();
        try
        {
            var result = await _requestCompactionHandler.HandleAsync(
                new RequestCompactionCommand(
                    sessionId,
                    workspaceId,
                    request.AgentId.Trim(),
                    request.Level ?? ContextCompactionLevel.Full,
                    request.Reason ?? "manual compact",
                    compactionId,
                    User.Identity?.Name),
                ct);

            return Ok(new CompactSessionResponse(
                result.CompactionId,
                result.Compaction,
                result.NewConversationId,
                result.NewConversationTitle));
        }
        catch (AgentConfigurationException ex)
        {
            _logger.LogWarning(
                ex,
                "[SessionEvents] Compact rejected invalid agent configuration agent={AgentId} errorCode={ErrorCode}",
                request.AgentId,
                ex.ErrorCode);
            return BadRequest(new
            {
                errorCode = ex.ErrorCode,
                message = ex.Message,
                agentId = ex.AgentId,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SessionEvents] Context compact failed session={Session}",
                sessionId);
            return Problem(title: "Context compaction failed", detail: ex.Message);
        }
    }

    /// <summary>
    /// 获取 SQLite 与 JSONL 双写一致性检查报告。
    /// GET /api/sessions/{sessionId}/consistency
    /// 关联 ADR：Docs/07架构/20会话状态机与事件规范ADR.md §6
    /// </summary>
    [HttpGet("{sessionId}/consistency")]
    public async Task<ActionResult<SessionConsistencyReport>> GetConsistency(
        string sessionId, CancellationToken ct)
    {
        var report = await _ssm.CheckConsistencyAsync(sessionId, ct);
        _logger.LogDebug(
            "[SessionEvents] GET consistency session={Session} consistent={Consistent} diff={Diff}",
            sessionId, report.IsConsistent, report.Difference);
        return Ok(report);
    }

    /// <summary>
    /// 获取会话级 Trace 聚合报告。
    /// GET /api/sessions/{sessionId}/trace-report
    /// 包含 traceId 列表、组件时序、LLM 调用、工具调用、子代理调用树及 token 总量。
    /// 关联 ADR：Docs/07架构/20会话状态机与事件规范ADR.md §6
    /// </summary>
    [HttpGet("{sessionId}/trace-report")]
        public async Task<ActionResult<SessionTraceReport>> GetTraceReport(
        string sessionId, [FromQuery] bool includeSubAgents = false, CancellationToken ct = default)
    {
        var report = await _ssm.GetTraceReportAsync(sessionId, includeSubAgents, ct);
        _logger.LogDebug(
            "[SessionEvents] GET trace-report session={Session} traces={TraceCount} llmCalls={LlmCount} toolCalls={ToolCount} subAgents={SubCount} durationMs={DurationMs} tokens={Tokens}",
            sessionId, report.TraceIds.Count, report.LlmCalls.Count, report.ToolCalls.Count, report.SubAgents.Count, report.TotalDurationMs, report.TotalTokens);
        return Ok(report);
    }

    /// <summary>
    /// 获取会话诊断日志（从文件系统读取 session 级别的 Serilog 日志）。
    /// GET /api/sessions/{sessionId}/diagnostics?lines=100
    /// </summary>
    [HttpGet("{sessionId}/diagnostics")]
    public ActionResult<object> GetDiagnostics(string sessionId, [FromQuery] int lines = 100)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "data", "logs", "sessions", sessionId);
            if (!Directory.Exists(logDir))
            {
                // 也尝试从配置的 logDir 查找
                var altLogDir = Path.Combine(Directory.GetCurrentDirectory(), "data", "logs", "sessions", sessionId);
                if (!Directory.Exists(altLogDir))
                    return Ok(new { sessionId, logs = Array.Empty<object>(), note = $"No session log directory found at {logDir} or {altLogDir}" });
                logDir = altLogDir;
            }

            var logFiles = Directory.GetFiles(logDir, "session-*.log")
                .OrderByDescending(f => f)
                .ToList();

            var entries = new List<object>();
            foreach (var file in logFiles.Take(1)) // 只读最新的日志文件
            {
                var fileLines = System.IO.File.ReadAllLines(file);
                var recentLines = fileLines.Reverse().Take(lines).Reverse();
                foreach (var line in recentLines)
                {
                    entries.Add(new { line });
                }
            }

            _logger.LogDebug("[SessionEvents] GET diagnostics session={Session} files={FileCount} entries={EntryCount}",
                sessionId, logFiles.Count, entries.Count);

            return Ok(new
            {
                sessionId,
                logDirectory = logDir,
                logFiles = logFiles.Select(Path.GetFileName),
                entries,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SessionEvents] Diagnostics failed session={Session}", sessionId);
            return Ok(new { sessionId, error = ex.Message });
        }
    }

    /// <summary>
    /// 获取会话级 prefix cache 诊断报告。
    /// GET /api/sessions/{sessionId}/cache-diagnostics?limit=50
    /// </summary>
    [HttpGet("{sessionId}/cache-diagnostics")]
    public async Task<ActionResult<CacheDiagnosticsReport>> GetCacheDiagnostics(
        string sessionId,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > 200)
            return BadRequest(new { message = "limit 必须在 1-200 之间" });

        var report = await _cacheDiagnosticsService.GetSessionReportAsync(sessionId, limit, ct);
        _logger.LogDebug(
            "[SessionEvents] GET cache-diagnostics session={Session} status={Status} events={EventCount} prefixes={PrefixCount}",
            sessionId, report.Status, report.AnalyzedEventCount, report.DistinctPrefixHashCount);
        return Ok(report);
    }

    // ── Session 存在性校验 ─────────────────────────

    /// <summary>
    /// 验证 session 是否在系统中存在，并返回其状态。
    /// 先查 Platform API（最快），失败后查本地 DB 中的历史证据（SessionEventLogs / ChatMessages / SessionSubAgents）。
    /// 用于防止对已删除的幻影 session 进行 replay 或 SSE 订阅。
    /// 关联 ADR：Docs/07架构/54ADR-053前端会话引用生命周期与SSE清理边界ADR.md
    /// </summary>
    /// <returns>SessionStatus 如果存在；null 如果不存在。</returns>
    private async Task<SessionStatus?> GetSessionStatusAsync(string sessionId, CancellationToken ct)
    {
        // 1. Platform API 快速检查
        var session = await _platformApi.GetSessionAsync(sessionId, ct);
        if (session is not null) return session.Status;

        // 2. 本地 DB 历史证据检查（backfill session）
        var hasEvents = await _db.SessionEventLogs.AnyAsync(e => e.SessionId == sessionId, ct);
        if (hasEvents) return SessionStatus.Completed; // backfill session 默认视为已完成

        var hasMessages = await _db.ChatMessages.AnyAsync(m => m.SessionId == sessionId, ct);
        if (hasMessages) return SessionStatus.Completed;

        var hasSubAgents = await _db.SessionSubAgents.AnyAsync(
            s => s.ParentSessionId == sessionId || s.SubSessionId == sessionId, ct);
        return hasSubAgents ? SessionStatus.Completed : null;
    }

    /// <summary>
    /// 验证 session 是否在系统中存在。
    /// </summary>
    private async Task<bool> SessionExistsAsync(string sessionId, CancellationToken ct)
        => await GetSessionStatusAsync(sessionId, ct) is not null;

    // ── SSE 工具方法 ───────────────────────────────────

    private static void ConfigureSseResponse(HttpResponse response)
        => SseResponseWriter.Configure(response);

    private static async Task WriteSseAsync(
        HttpResponse response,
        ServerSentEventFrame frame,
        string sessionId,
        ILogger logger,
        ISessionTimelineRecorder timelineRecorder,
        long frameIndex,
        CancellationToken ct)
    {
        await WriteSseFrameAsync(response, frame, sessionId, logger, frameIndex, ct);
        await FlushSseBatchAsync(
            response,
            new SseFrameBatchFlush(
                frameIndex,
                frameIndex,
                1,
                frame.Data.Length,
                frame.Event,
                frame.Event,
                "single_frame"),
            sessionId,
            logger,
            timelineRecorder,
            ct);
    }

    private static async Task WriteSseFrameAsync(
        HttpResponse response,
        ServerSentEventFrame frame,
        string sessionId,
        ILogger logger,
        long frameIndex,
        CancellationToken ct)
    {
        var seq = TryReadSequenceNum(frame.Data);
        logger.LogDebug(
            "[SessionEvents] SSE write begin session={Session} index={Index} type={EventType} seq={Seq} dataChars={DataChars}",
            sessionId, frameIndex, frame.Event, seq, frame.Data.Length);
        await SseResponseWriter.WriteFrameAsync(response, frame, ct);
    }

    private static async Task FlushSseBatchAsync(
        HttpResponse response,
        SseFrameBatchFlush flush,
        string sessionId,
        ILogger logger,
        ISessionTimelineRecorder timelineRecorder,
        CancellationToken ct)
    {
        var flushWatch = Stopwatch.StartNew();
        await response.Body.FlushAsync(ct);
        flushWatch.Stop();
        if (flushWatch.ElapsedMilliseconds >= 250)
        {
            logger.LogWarning(
                "[SessionEvents] SSE batch flush slow session={Session} first={FirstIndex} last={LastIndex} frames={FrameCount} reason={Reason} elapsedMs={ElapsedMs}",
                sessionId,
                flush.FirstFrameIndex,
                flush.LastFrameIndex,
                flush.FrameCount,
                flush.Reason,
                flushWatch.ElapsedMilliseconds);
        }
        else
        {
            logger.LogDebug(
                "[SessionEvents] SSE batch flushed session={Session} first={FirstIndex} last={LastIndex} frames={FrameCount} reason={Reason} elapsedMs={ElapsedMs}",
                sessionId,
                flush.FirstFrameIndex,
                flush.LastFrameIndex,
                flush.FrameCount,
                flush.Reason,
                flushWatch.ElapsedMilliseconds);
        }

        if (ShouldRecordSseFlush(flush, flushWatch.ElapsedMilliseconds))
        {
            await RecordSseTimelineAsync(
                timelineRecorder,
                sessionId,
                "sse.batch.flush.completed",
                "sse.flush",
                RuntimeActivityStatuses.Succeeded,
                new Dictionary<string, string>
                {
                    ["firstFrameIndex"] = flush.FirstFrameIndex.ToString(),
                    ["lastFrameIndex"] = flush.LastFrameIndex.ToString(),
                    ["frameCount"] = flush.FrameCount.ToString(),
                    ["firstEventType"] = flush.FirstEvent,
                    ["lastEventType"] = flush.LastEvent,
                    ["dataChars"] = flush.DataChars.ToString(),
                    ["reason"] = flush.Reason,
                },
                logger,
                ct,
                flushWatch.ElapsedMilliseconds);
        }
    }

    private static bool ShouldRecordSseFlush(SseFrameBatchFlush flush, long elapsedMs) =>
        elapsedMs >= 250
        || flush.Reason is
            SseFlushReasons.Terminal or
            SseFlushReasons.FrameCount or
            SseFlushReasons.Bytes or
            SseFlushReasons.ChannelCompleted
        || flush.FrameCount >= 8
        || flush.DataChars >= 1024;

    private static async Task RecordSseTimelineAsync(
        ISessionTimelineRecorder timelineRecorder,
        string sessionId,
        string stage,
        string operation,
        string status,
        IReadOnlyDictionary<string, string>? metadata,
        ILogger logger,
        CancellationToken ct,
        long? durationMs = null)
    {
        try
        {
            await timelineRecorder.RecordAsync(new SessionTimelineRecord
            {
                Trace = RuntimeTraceContext.CreateNew(sessionId: sessionId),
                Component = RuntimeActivityComponents.SessionState,
                Stage = stage,
                Operation = operation,
                Status = status,
                DurationMs = durationMs,
                Metadata = metadata,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SessionEvents] Timeline record failed stage={Stage}", stage);
        }
    }

    private static long? TryReadSequenceNum(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sequenceNum", out var seq) &&
                seq.TryGetInt64(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// 获取会话 per-turn 成本明细。
    /// GET /api/sessions/{sessionId}/turn-costs
    /// </summary>
    [HttpGet("{sessionId}/turn-costs")]
    public async Task<ActionResult<List<TurnCostRow>>> GetTurnCosts(
        string sessionId, CancellationToken ct)
    {
        var rows = await _tokenCostService.GetTurnCostsAsync(sessionId, ct);
        return Ok(rows);
    }

    /// <summary>
    /// 获取 per-tool 成本聚合。
    /// GET /api/stats/tool-costs?workspaceId=default&providerId=deepseek&from=2026-07-01&to=2026-07-11
    /// </summary>
    [HttpGet("/api/stats/tool-costs")]
    public async Task<ActionResult<List<ToolCostRow>>> GetToolCosts(
        [FromQuery] string? workspaceId,
        [FromQuery] string? providerId,
        [FromQuery] string? modelId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var rows = await _tokenCostService.GetToolCostsAsync(
            workspaceId, providerId, modelId, from, to, ct);
        return Ok(rows);
    }

    /// <summary>
    /// 获取每日成本汇总（与 DeepSeek 后台对比）。
    /// GET /api/stats/daily-cost?date=2026-07-11
    /// </summary>
    [HttpGet("/api/stats/daily-cost")]
    public async Task<ActionResult<DailyCostSummary>> GetDailyCost(
        [FromQuery] DateTimeOffset? date, CancellationToken ct)
    {
        var targetDate = date ?? DateTimeOffset.UtcNow.Date;
        var summary = await _tokenCostService.GetDailySummaryAsync(targetDate, ct);
        return Ok(summary);
    }

    /// ADR-057 Phase 7: 手动触发投影。
    /// POST /api/sessions/{sessionId}/project
    /// </summary>
    [AllowAnonymous]
    [HttpPost("{sessionId}/project")]
    public async Task<ActionResult<ProjectionResult>> TriggerProjection(
        string sessionId, CancellationToken ct)
    {
        var projector = HttpContext.RequestServices.GetService<ConversationProjector>();
        if (projector is null)
            return StatusCode(503, new { error = "projector_unavailable" });

        var result = await projector.ProjectAsync(sessionId, ct);
        return Ok(result);
    }

    private sealed record ContextAgentBindingRow(
        string WorkspaceId,
        string AgentInstanceId);

    private sealed record ResolvedContextCapacity(
        int ContextWindowTokens,
        int? MaxOutputTokens);
}

public sealed record CompactSessionRequest(
    string? WorkspaceId,
    string? AgentId,
    ContextCompactionLevel? Level,
    string? Reason,
    string? CompactionId);

/// <summary>
/// 压缩会话的响应，包含压缩结果和可选的新会话信息。
/// 压缩成功后会创建新 Session，前端应切换到新 Session 继续对话。
/// </summary>
public sealed record CompactSessionResponse(
    string CompactionId,
    ContextCompactionResult Compaction,
    string NewSessionId,
    string? NewSessionTitle);
