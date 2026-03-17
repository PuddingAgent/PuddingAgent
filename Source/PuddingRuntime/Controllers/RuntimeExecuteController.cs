using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntime.Controllers;

/// <summary>
/// Runtime 执行 API——接收 Controller 的 RuntimeDispatchRequest，
/// 调用 Agent 执行并返回结果。
/// </summary>
[ApiController]
[Route("api/runtime")]
public class RuntimeExecuteController : ControllerBase
{
    private readonly AgentExecutionService _executionService;
    private readonly AgentSessionManager _sessionManager;
    private readonly ILogger<RuntimeExecuteController> _logger;

    public RuntimeExecuteController(
        AgentExecutionService executionService,
        AgentSessionManager sessionManager,
        ILogger<RuntimeExecuteController> logger)
    {
        _executionService = executionService;
        _sessionManager = sessionManager;
        _logger = logger;
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
        _logger.LogInformation("[RuntimeAPI] Execute session={SessionId}", request.SessionId);

        var result = await _executionService.ExecuteAsync(request, ct);
        return Ok(result);
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
}
