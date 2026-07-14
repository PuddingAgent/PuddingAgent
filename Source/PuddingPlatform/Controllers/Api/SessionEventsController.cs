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
    private static readonly SseFrameBatchPump SsePump = new();

    private readonly ISessionStateManager _ssm;
    private readonly IContextCompactionService _compactionService;
    private readonly CacheDiagnosticsService _cacheDiagnosticsService;
    private readonly ISessionTimelineRecorder _timelineRecorder;
    private readonly PlatformApiClient _platformApi;
    private readonly SessionRedirectStore _redirectStore;
    private readonly PlatformDbContext _db;
    private readonly IAgentRuntimeProfileResolver _agentRuntimeProfileResolver;
    private readonly ILlmConfigService _llmConfigService;
    private readonly IRawSessionLogService _rawLogs;
    private readonly TokenCostService _tokenCostService;
    private readonly ILogger<SessionEventsController> _logger;

    public SessionEventsController(
        ISessionStateManager ssm,
        IContextCompactionService compactionService,
        CacheDiagnosticsService cacheDiagnosticsService,
        ISessionTimelineRecorder timelineRecorder,
        PlatformApiClient platformApi,
        SessionRedirectStore redirectStore,
        PlatformDbContext db,
        IAgentRuntimeProfileResolver agentRuntimeProfileResolver,
        ILlmConfigService llmConfigService,
        IRawSessionLogService rawLogs,
        TokenCostService tokenCostService,
        ILogger<SessionEventsController> logger)
    {
        _ssm = ssm;
        _compactionService = compactionService;
        _cacheDiagnosticsService = cacheDiagnosticsService;
        _timelineRecorder = timelineRecorder;
        _platformApi = platformApi;
        _redirectStore = redirectStore;
        _db = db;
        _agentRuntimeProfileResolver = agentRuntimeProfileResolver;
        _llmConfigService = llmConfigService;
        _rawLogs = rawLogs;
        _tokenCostService = tokenCostService;
        _logger = logger;
    }

    /// <summary>
    /// 获取会话事件历史（分页/游标加载）。
    /// GET /api/sessions/{sessionId}/events?from={seq}&limit={N}
    /// </summary>
    [HttpGet("{sessionId}/events")]
    public async Task<ActionResult<SessionEventPage>> GetEvents(
        string sessionId,
        [FromQuery] long? from,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > 200)
            return BadRequest(new { message = "limit 必须在 1-200 之间" });

        var page = await _ssm.GetEventsAsync(sessionId, from, limit, ct);

        _logger.LogDebug(
            "[SessionEvents] GET history session={Session} from={From} limit={Limit} count={Count} hasMore={HasMore}",
            sessionId, from, limit, page.Events.Count, page.HasMore);

        return Ok(page);
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
            return;
        }

        ConfigureSseResponse(Response);

        var reader = _ssm.Subscribe(sessionId);
        if (reader == null)
        {
            _logger.LogWarning(
                "[SessionEvents] SSE subscribe closed session={Session}; no channel is available",
                sessionId);
            // 会话已关闭且无 Channel → 返回 session.closed 事件后结束
            await WriteSseAsync(Response, new ServerSentEventFrame(
                    SessionEventTypes.SessionClosed,
                    JsonSerializer.Serialize(new { sessionId })),
                sessionId,
                _logger,
                _timelineRecorder,
                1,
                ct);
            return;
        }

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

        // Phase 0: 断线重连时补发缺口事件（订阅后、实时推送前）
        long lastReplayedSeq = 0;
        if (afterSequence.HasValue)
        {
            try
            {
                var replayed = 0L;
                var page = await _ssm.GetEventsAsync(sessionId, afterSequence, limit: 200, ct);
                foreach (var entry in page.Events)
                {
                    if (replayed >= 200) break;
                    var replayFrame = new ServerSentEventFrame(entry.EventType, entry.Data);
                    await WriteSseFrameAsync(Response, replayFrame, sessionId, _logger, replayed, ct);
                    lastReplayedSeq = entry.SequenceNum;
                    replayed++;
                    if (replayed % 50 == 0) await Response.Body.FlushAsync(ct);
                }
                await Response.Body.FlushAsync(ct);
                _logger.LogInformation(
                    "[SessionEvents] SSE replay complete session={Session} after={AfterSeq} lastReplayed={LastSeq} count={Count}",
                    sessionId, afterSequence, lastReplayedSeq, replayed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[SessionEvents] SSE replay failed session={Session} after={AfterSeq}",
                    sessionId, afterSequence);
            }
        }

        try
        {
            await SsePump.PumpAsync(
                reader,
                (frame, frameIndex, token) => WriteSseFrameAsync(Response, frame, sessionId, _logger, frameIndex, token),
                (flush, token) => FlushSseBatchAsync(Response, flush, sessionId, _logger, _timelineRecorder, token),
                ct);
        }
        catch (OperationCanceledException)
        {
            // 客户端主动取消（刷新页面、关闭标签）——正常行为
            _logger.LogDebug(
                "[SessionEvents] SSE disconnected session={Session}", sessionId);
        }
        catch (IOException)
        {
            // 客户端断连导致写入失败（broken pipe / connection reset）——正常行为，不计入 Session Fuse
            _logger.LogDebug(
                "[SessionEvents] SSE write failed (client disconnected) session={Session}", sessionId);
        }
        finally
        {
            _ssm.Unsubscribe(sessionId, reader);
        }
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
    public async Task<ActionResult<ContextCompactionResult>> Compact(
        string sessionId,
        [FromBody] CompactSessionRequest request,
        CancellationToken ct)
    {
        var workspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId)
            ? "default"
            : request.WorkspaceId;
        var resolvedAgentId = request.AgentId ?? "default.global_general-assistant.823";

        // Resolve LlmConfig from agent runtime profile.
        // Without this, AgentContextCompactionSummaryGenerator fails with
        // "Agent LLM config is null" because the compaction summary needs an LLM.
        LlmConfig? llmConfig = null;
        try
        {
            var profile = await _agentRuntimeProfileResolver.ResolveAsync(workspaceId, resolvedAgentId, ct);
            llmConfig = profile.LlmConfig;
            _logger.LogInformation(
                "[SessionEvents] Compact resolved LlmConfig agent={AgentId} provider={Provider} model={Model}",
                resolvedAgentId, profile.PreferredProviderId, profile.PreferredModelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SessionEvents] Compact failed to resolve LlmConfig agent={AgentId}", resolvedAgentId);
        }

        var compactRequest = new ContextCompactionRequest(
            workspaceId,
            sessionId,
            resolvedAgentId,
            ContextCompactionMode.Manual,
            request.Level ?? ContextCompactionLevel.Full,
            request.Reason ?? "manual compact")
        {
            LlmConfig = llmConfig
        };

        await _ssm.AppendAsync(
            sessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.ContextCompactionStarted, new
            {
                sessionId,
                mode = compactRequest.Mode.ToString(),
                level = compactRequest.Level.ToString(),
                reason = compactRequest.Reason,
            }),
            ct);

        try
        {
            var result = await _compactionService.CompactAsync(compactRequest, ct);

            // 压缩成功后创建新 Session，让后续对话在新 Session 中进行
            string? newSessionId = null;
            SessionRecord? newSession = null;
            try
            {
                var oldSession = await _platformApi.GetSessionAsync(sessionId, ct);
                var agentTemplateId = oldSession?.AgentTemplateId ?? request.AgentId ?? "global:code-assistant";
                var title = oldSession?.Title is { Length: > 0 } t
                    ? $"压缩 - {t}"
                    : "压缩后的新会话";
                newSession = await _platformApi.CreateSessionAsync(
                    workspaceId,
                    agentTemplateId,
                    title,
                    ct: ct);
                newSessionId = newSession?.SessionId;

                if (newSessionId is not null)
                {
                    var agentId = request.AgentId ?? oldSession?.AgentTemplateId ?? agentTemplateId;
                    _redirectStore.Register(workspaceId, agentId, sessionId, newSessionId);
                    _logger.LogInformation(
                        "[SessionEvents] Compact created new session old={OldSession} new={NewSession}",
                        sessionId,
                        newSessionId);
                }
            }
            catch (Exception ex)
            {
                // 新 Session 创建失败不影响 compact 本身的结果
                _logger.LogWarning(ex,
                    "[SessionEvents] Compact succeeded but new session creation failed old={OldSession}",
                    sessionId);
            }

            var response = new CompactSessionResponse(result, newSessionId, newSession?.Title);
            await _ssm.AppendAsync(
                sessionId,
                workspaceId,
                ServerSentEventFrame.Json(SseEventTypes.ContextCompactionCompleted, response),
                ct);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SessionEvents] Context compact failed session={Session}",
                sessionId);
            await _ssm.AppendAsync(
                sessionId,
                workspaceId,
                ServerSentEventFrame.Json(SseEventTypes.ContextCompactionFailed, new
                {
                    sessionId,
                    error = ex.Message,
                }),
                CancellationToken.None);
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
        string sessionId, CancellationToken ct)
    {
        var report = await _ssm.GetTraceReportAsync(sessionId, ct);
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
    string? Reason);

/// <summary>
/// 压缩会话的响应，包含压缩结果和可选的新会话信息。
/// 压缩成功后会创建新 Session，前端应切换到新 Session 继续对话。
/// </summary>
public sealed record CompactSessionResponse(
    ContextCompactionResult Compaction,
    string? NewSessionId,
    string? NewSessionTitle);
