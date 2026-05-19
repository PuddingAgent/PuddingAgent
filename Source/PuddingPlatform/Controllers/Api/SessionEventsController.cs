using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

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
    private readonly ILogger<SessionEventsController> _logger;

    public SessionEventsController(
        ISessionStateManager ssm,
        ILogger<SessionEventsController> logger)
    {
        _ssm = ssm;
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

        var result = await _ssm.ReplaySessionAsync(sessionId, from, limit, ct);

        _logger.LogDebug(
            "[SessionEvents] GET replay session={Session} from={From} limit={Limit} events={EventCount} total={Total} hasMore={HasMore}",
            sessionId, from, limit, result.Events.Count, result.TotalEventCount, result.HasMore);

        return Ok(result);
    }

    /// <summary>
    /// 实时订阅会话事件（SSE）。
    /// GET /api/sessions/{sessionId}/events/stream
    /// </summary>
    [HttpGet("{sessionId}/events/stream")]
    public async Task EventsStream(string sessionId, CancellationToken ct)
    {
        ConfigureSseResponse(Response);

        var reader = _ssm.Subscribe(sessionId);
        if (reader == null)
        {
            // 会话已关闭且无 Channel → 返回 session.closed 事件后结束
            await WriteSseAsync(Response, new ServerSentEventFrame(
                SessionEventTypes.SessionClosed,
                System.Text.Json.JsonSerializer.Serialize(new { sessionId })), ct);
            return;
        }

        _logger.LogInformation(
            "[SessionEvents] SSE subscribed session={Session}", sessionId);

        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                await WriteSseAsync(Response, frame, ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "[SessionEvents] SSE disconnected session={Session}", sessionId);
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

    // ── SSE 工具方法 ───────────────────────────────────

    private static void ConfigureSseResponse(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteSseAsync(
        HttpResponse response,
        ServerSentEventFrame frame,
        CancellationToken ct)
    {
        await response.WriteAsync($"event: {frame.Event}\n", ct);
        await response.WriteAsync($"data: {frame.Data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
