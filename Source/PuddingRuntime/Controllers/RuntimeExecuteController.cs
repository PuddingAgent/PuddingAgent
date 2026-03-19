using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Controllers;

/// <summary>
/// Runtime 执行 API——接收 Controller 的 RuntimeDispatchRequest，
/// 调用 Agent 执行并返回结果。
/// 同时暴露 Cancel / Freeze / Resume / Wakeup 四类治理控制端点。
/// </summary>
[ApiController]
[Route("api/runtime")]
public class RuntimeExecuteController : ControllerBase
{
    private readonly AgentExecutionService _executionService;
    private readonly AgentSessionManager _sessionManager;
    private readonly ExecutionControlRegistry _controlRegistry;
    private readonly ExecutionJournal _journal;
    private readonly ILogger<RuntimeExecuteController> _logger;

    public RuntimeExecuteController(
        AgentExecutionService executionService,
        AgentSessionManager sessionManager,
        ExecutionControlRegistry controlRegistry,
        ExecutionJournal journal,
        ILogger<RuntimeExecuteController> logger)
    {
        _executionService = executionService;
        _sessionManager   = sessionManager;
        _controlRegistry  = controlRegistry;
        _journal          = journal;
        _logger           = logger;
    }

    /// <summary>
    /// POST /api/runtime/execute
    /// Controller 调用此端点将消息交给 Agent 执行。
    /// </summary>
    [HttpPost("execute")]
    public async Task<ActionResult<RuntimeDispatchResult>> Execute(
        [FromBody] RuntimeDispatchRequest request,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation(
            "[RuntimeAPI] Execute session={SessionId} ws={Ws} template={Template} hasLlmConfig={HasConfig}",
            request.SessionId, request.WorkspaceId, request.AgentTemplateId, request.LlmConfig is not null);

        var result = await _executionService.ExecuteAsync(request, ct);
        sw.Stop();

        if (result.IsSuccess)
            _logger.LogInformation(
                "[RuntimeAPI] OK session={SessionId} replyLen={Len} elapsed={Elapsed}ms",
                request.SessionId, result.ReplyText?.Length ?? 0, sw.ElapsedMilliseconds);
        else
            _logger.LogWarning(
                "[RuntimeAPI] FAILED session={SessionId} elapsed={Elapsed}ms error={Error}",
                request.SessionId, sw.ElapsedMilliseconds, result.ErrorMessage);

        return Ok(result);
    }

    /// <summary>
    /// POST /api/runtime/execute/wakeup
    /// 事件命中后，Controller 投递此请求以恢复 WAIT 态的 Agent 会话。
    /// </summary>
    [HttpPost("execute/wakeup")]
    public async Task<ActionResult<RuntimeDispatchResult>> Wakeup(
        [FromBody] DispatchWakeupRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "[RuntimeAPI] Wakeup session={SessionId} eventType={EventType} anchorId={AnchorId}",
            request.SessionId, request.EventType, request.ResumeAnchorId);

        var result = await _executionService.ExecuteWakeupAsync(request, ct);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// POST /api/runtime/sessions/{sessionId}/cancel
    /// 向正在运行的 Agent Loop 发出取消信号。
    /// </summary>
    [HttpPost("sessions/{sessionId}/cancel")]
    public ActionResult Cancel(string sessionId, [FromBody] CancelExecutionRequest request)
    {
        _logger.LogInformation(
            "[RuntimeAPI] Cancel session={SessionId} reason={Reason}", sessionId, request.Reason);
        _controlRegistry.Cancel(sessionId);
        return Ok(new { sessionId, action = "cancel", reason = request.Reason });
    }

    /// <summary>
    /// POST /api/runtime/sessions/{sessionId}/freeze
    /// 冻结目标 Session——治理优先级最高，立即停止执行。
    /// </summary>
    [HttpPost("sessions/{sessionId}/freeze")]
    public ActionResult Freeze(string sessionId, [FromBody] FreezeExecutionRequest request)
    {
        _logger.LogInformation(
            "[RuntimeAPI] Freeze session={SessionId} reason={Reason}", sessionId, request.Reason);
        _controlRegistry.Freeze(sessionId);
        return Ok(new { sessionId, action = "freeze", reason = request.Reason });
    }

    /// <summary>
    /// POST /api/runtime/sessions/{sessionId}/resume
    /// 解除目标 Session 的冻结标志（不自动重启执行；需由 Controller 重新投递执行请求）。
    /// </summary>
    [HttpPost("sessions/{sessionId}/resume")]
    public ActionResult Resume(string sessionId, [FromBody] ResumeExecutionRequest request)
    {
        _logger.LogInformation("[RuntimeAPI] Resume session={SessionId}", sessionId);
        _controlRegistry.Resume(sessionId);
        return Ok(new { sessionId, action = "resume" });
    }

    /// <summary>
    /// GET /api/runtime/sessions
    /// 列出所有活跃 Agent 实例。
    /// </summary>
    [HttpGet("sessions")]
    public ActionResult GetActiveSessions()
    {
        var sessions = _sessionManager.ListActive();
        return Ok(sessions);
    }

    /// <summary>
    /// GET /api/runtime/sessions/{sessionId}/anchor
    /// 查询指定 Session 的 ResumeAnchor（用于调试 / 监控 WAIT 态）。
    /// </summary>
    [HttpGet("sessions/{sessionId}/anchor")]
    public ActionResult GetAnchor(string sessionId)
    {
        var anchor = _journal.GetAnchor(sessionId);
        return anchor is null ? NotFound() : Ok(anchor);
    }
}
