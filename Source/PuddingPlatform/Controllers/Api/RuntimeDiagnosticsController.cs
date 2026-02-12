using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Observability;

namespace PuddingPlatform.Controllers.Api;

[Authorize]
[ApiController]
[Route("api/runtime")]
public sealed class RuntimeDiagnosticsController : ControllerBase
{
    private readonly IRuntimeActivitySink _activitySink;
    private readonly ILogger<RuntimeDiagnosticsController> _logger;

    public RuntimeDiagnosticsController(
        IRuntimeActivitySink activitySink,
        ILogger<RuntimeDiagnosticsController> logger)
    {
        _activitySink = activitySink;
        _logger = logger;
    }

    [HttpGet("activities")]
    public async Task<ActionResult<IReadOnlyList<RuntimeActivity>>> GetActivities(
        [FromQuery] string? traceId,
        [FromQuery] string? sessionId,
        [FromQuery] string? executionId,
        [FromQuery] string? component,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var query = new RuntimeActivityQuery
        {
            TraceId = traceId,
            SessionId = sessionId,
            ExecutionId = executionId,
            Component = component,
            Limit = Math.Clamp(limit, 1, 500),
        };

        var activities = await _activitySink.QueryAsync(query, ct);

        _logger.LogDebug(
            "[RuntimeDiagnostics] GET activities trace={TraceId} session={SessionId} execution={ExecutionId} component={Component} count={Count}",
            traceId,
            sessionId,
            executionId,
            component,
            activities.Count);

        return Ok(activities);
    }
}
