using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Orchestration;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Read-only control-plane API for orchestration discovery, immutable definitions, run projection,
/// durable event replay, and replay-to-live SSE. Mutating commands remain outside this surface.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orchestrations")]
public sealed class AgentOrchestrationApiController(
    IAgentOrchestrationComponentRegistry componentRegistry,
    IAgentOrchestrationQueryStore queryStore,
    AgentOrchestrationEventFollower eventFollower,
    ILogger<AgentOrchestrationApiController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        AgentOrchestrationJson.CreateSerializerOptions();

    /// <summary>Component and trigger descriptors used by the Admin palette and graph compiler.</summary>
    [HttpGet("catalog")]
    public ActionResult<AgentOrchestrationCatalogDto> GetCatalog()
        => new JsonResult(new AgentOrchestrationCatalogDto
        {
            SchemaVersion = AgentOrchestrationSchemas.GraphDefinitionV2,
            Components = componentRegistry.Components,
            Triggers = componentRegistry.Triggers
        }, JsonOptions);

    /// <summary>Current graph heads for Admin discovery, newest updates first.</summary>
    [HttpGet("graphs")]
    public async Task<ActionResult<AgentOrchestrationGraphPageDto>> ListGraphs(
        [FromQuery] string? workspaceId = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 200)
            return BadRequest(new { code = "orchestration.invalid_limit", message = "limit must be between 1 and 200." });
        if (offset < 0)
            return BadRequest(new { code = "orchestration.invalid_offset", message = "offset cannot be negative." });

        var fetched = await queryStore.ListGraphsAsync(workspaceId, limit + 1, offset, ct);
        var graphs = fetched.Take(limit).ToArray();
        return new JsonResult(new AgentOrchestrationGraphPageDto
        {
            WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim(),
            Offset = offset,
            Count = graphs.Length,
            HasMore = fetched.Count > limit,
            Graphs = graphs
        }, JsonOptions);
    }

    /// <summary>Lightweight run projections for graph/workspace/status discovery.</summary>
    [HttpGet("runs")]
    public async Task<ActionResult<AgentOrchestrationRunPageDto>> ListRuns(
        [FromQuery] string? workspaceId = null,
        [FromQuery] string? graphId = null,
        [FromQuery] AgentOrchestrationRunStatus? status = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 200)
            return BadRequest(new { code = "orchestration.invalid_limit", message = "limit must be between 1 and 200." });
        if (offset < 0)
            return BadRequest(new { code = "orchestration.invalid_offset", message = "offset cannot be negative." });

        var fetched = await queryStore.ListRunsAsync(workspaceId, graphId, status, limit + 1, offset, ct);
        var runs = fetched.Take(limit).ToArray();
        return new JsonResult(new AgentOrchestrationRunPageDto
        {
            WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim(),
            GraphId = string.IsNullOrWhiteSpace(graphId) ? null : graphId.Trim(),
            Status = status,
            Offset = offset,
            Count = runs.Length,
            HasMore = fetched.Count > limit,
            Runs = runs
        }, JsonOptions);
    }

    /// <summary>Newest immutable definition for a graph.</summary>
    [HttpGet("graphs/{graphId}/latest")]
    public async Task<ActionResult<AgentOrchestrationGraphDefinition>> GetLatestRevision(
        string graphId,
        CancellationToken ct = default)
    {
        var definition = await queryStore.GetLatestRevisionAsync(graphId, ct);
        return definition is null
            ? NotFound(new { code = "orchestration.graph_not_found", graphId })
            : new JsonResult(definition, JsonOptions);
    }

    /// <summary>Revision history ordered newest first.</summary>
    [HttpGet("graphs/{graphId}/revisions")]
    public async Task<ActionResult<AgentOrchestrationRevisionPageDto>> ListRevisions(
        string graphId,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 500)
            return BadRequest(new { code = "orchestration.invalid_limit", message = "limit must be between 1 and 500." });

        var revisions = await queryStore.ListRevisionsAsync(graphId, limit, ct);
        if (revisions.Count == 0 && await queryStore.GetLatestRevisionAsync(graphId, ct) is null)
            return NotFound(new { code = "orchestration.graph_not_found", graphId });

        return new JsonResult(new AgentOrchestrationRevisionPageDto
        {
            GraphId = graphId,
            Revisions = revisions,
            Count = revisions.Count
        }, JsonOptions);
    }

    /// <summary>One immutable executable graph definition.</summary>
    [HttpGet("revisions/{**revisionId}")]
    public async Task<ActionResult<AgentOrchestrationGraphDefinition>> GetRevision(
        string revisionId,
        CancellationToken ct = default)
    {
        var definition = await queryStore.GetRevisionAsync(revisionId, ct);
        return definition is null
            ? NotFound(new { code = "orchestration.revision_not_found", revisionId })
            : new JsonResult(definition, JsonOptions);
    }

    /// <summary>Current durable projection of a run and all node runs.</summary>
    [HttpGet("runs/{runId}")]
    public async Task<ActionResult<AgentOrchestrationRunSnapshot>> GetRun(
        string runId,
        CancellationToken ct = default)
    {
        var run = await queryStore.GetRunAsync(runId, ct);
        return run is null
            ? NotFound(new { code = "orchestration.run_not_found", runId })
            : new JsonResult(run, JsonOptions);
    }

    /// <summary>Cursor catch-up from the append-only orchestration event log.</summary>
    [HttpGet("runs/{runId}/events")]
    public async Task<ActionResult<AgentOrchestrationEventPageDto>> GetEvents(
        string runId,
        [FromQuery] long afterSequence = 0,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        if (afterSequence < 0)
            return BadRequest(new { code = "orchestration.invalid_cursor", message = "afterSequence cannot be negative." });
        if (limit is < 1 or > 500)
            return BadRequest(new { code = "orchestration.invalid_limit", message = "limit must be between 1 and 500." });

        var run = await queryStore.GetRunAsync(runId, ct);
        if (run is null)
            return NotFound(new { code = "orchestration.run_not_found", runId });
        if (afterSequence > run.HeadSequence)
        {
            return BadRequest(new
            {
                code = "orchestration.cursor_ahead",
                afterSequence,
                headSequence = run.HeadSequence
            });
        }

        var fetched = await queryStore.GetEventsAfterAsync(runId, afterSequence, limit + 1, ct);
        var events = fetched.Take(limit).ToArray();
        var nextSequence = events.Length == 0 ? afterSequence : events[^1].Sequence;
        var observedHead = Math.Max(run.HeadSequence, fetched.LastOrDefault()?.Sequence ?? 0);
        return new JsonResult(new AgentOrchestrationEventPageDto
        {
            RunId = runId,
            AfterSequence = afterSequence,
            NextSequence = nextSequence,
            HeadSequence = observedHead,
            HasMore = fetched.Count > limit || nextSequence < observedHead,
            Events = events
        }, JsonOptions);
    }

    /// <summary>
    /// Replay committed events after a cursor, then follow live commits without a query/subscribe gap.
    /// Last-Event-ID is used only when afterSequence is absent.
    /// </summary>
    [HttpGet("runs/{runId}/watch")]
    public async Task Watch(
        string runId,
        [FromQuery] long? afterSequence = null,
        CancellationToken ct = default)
    {
        var cursor = ResolveCursor(afterSequence);
        if (cursor is null || cursor < 0)
        {
            await WriteJsonErrorAsync(
                StatusCodes.Status400BadRequest,
                "orchestration.invalid_cursor",
                "afterSequence and Last-Event-ID must be non-negative integers.",
                ct);
            return;
        }

        var run = await queryStore.GetRunAsync(runId, ct);
        if (run is null)
        {
            await WriteJsonErrorAsync(
                StatusCodes.Status404NotFound,
                "orchestration.run_not_found",
                $"Run '{runId}' was not found.",
                ct);
            return;
        }
        if (cursor > run.HeadSequence)
        {
            await WriteJsonErrorAsync(
                StatusCodes.Status400BadRequest,
                "orchestration.cursor_ahead",
                $"Cursor {cursor} is ahead of committed head {run.HeadSequence}.",
                ct);
            return;
        }

        ConfigureSseResponse(Response);
        logger.LogInformation(
            "[AgentOrchestrationApi] Watch subscribed run={RunId} after={AfterSequence}",
            runId,
            cursor);

        try
        {
            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            await using var events = eventFollower
                .FollowAsync(runId, cursor.Value, ct)
                .GetAsyncEnumerator(ct);
            var nextEvent = events.MoveNextAsync().AsTask();
            var nextHeartbeat = heartbeatTimer.WaitForNextTickAsync(ct).AsTask();

            while (true)
            {
                var completed = await Task.WhenAny(nextEvent, nextHeartbeat);
                if (completed == nextEvent)
                {
                    if (!await nextEvent)
                        break;
                    await WriteEventAsSseAsync(Response, events.Current, ct);
                    nextEvent = events.MoveNextAsync().AsTask();
                    continue;
                }

                if (!await nextHeartbeat)
                    break;
                await WriteHeartbeatAsSseAsync(Response, ct);
                nextHeartbeat = heartbeatTimer.WaitForNextTickAsync(ct).AsTask();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug("[AgentOrchestrationApi] Watch disconnected run={RunId}", runId);
        }
        catch (IOException)
        {
            logger.LogDebug("[AgentOrchestrationApi] Watch write failed run={RunId}", runId);
        }
        catch (AgentOrchestrationEventGapException ex)
        {
            logger.LogError(
                ex,
                "[AgentOrchestrationApi] Durable event gap run={RunId} expected={Expected} observed={Observed}",
                ex.RunId,
                ex.ExpectedSequence,
                ex.ActualOrHeadSequence);
            if (!Response.HasStarted)
            {
                await WriteJsonErrorAsync(
                    StatusCodes.Status409Conflict,
                    "orchestration.event_gap",
                    ex.Message,
                    ct);
            }
            else
            {
                await WriteStreamErrorAsSseAsync(Response, "orchestration.event_gap", ex.Message, ct);
            }
        }
    }

    private long? ResolveCursor(long? afterSequence)
    {
        if (afterSequence.HasValue)
            return afterSequence.Value;

        var value = Request.Headers["Last-Event-ID"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static void ConfigureSseResponse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteEventAsSseAsync(
        HttpResponse response,
        AgentOrchestrationRunEvent envelope,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var frame = $"id: {envelope.Sequence}\nevent: {envelope.EventType}\ndata: {json}\n\n";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteHeartbeatAsSseAsync(HttpResponse response, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(": heartbeat\n\n"), ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteStreamErrorAsSseAsync(
        HttpResponse response,
        string code,
        string message,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { code, message }, JsonOptions);
        var frame = $"event: orchestration.stream.error\ndata: {json}\n\n";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
        await response.Body.FlushAsync(ct);
    }

    private async Task WriteJsonErrorAsync(
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(new { code, message }, JsonOptions), ct);
    }
}

public sealed record AgentOrchestrationCatalogDto
{
    public required string SchemaVersion { get; init; }
    public IReadOnlyList<AgentOrchestrationRegisteredComponent> Components { get; init; }
        = Array.Empty<AgentOrchestrationRegisteredComponent>();
    public IReadOnlyList<AgentOrchestrationRegisteredTrigger> Triggers { get; init; }
        = Array.Empty<AgentOrchestrationRegisteredTrigger>();
}

public sealed record AgentOrchestrationGraphPageDto
{
    public string? WorkspaceId { get; init; }
    public int Offset { get; init; }
    public int Count { get; init; }
    public bool HasMore { get; init; }
    public IReadOnlyList<AgentOrchestrationGraphSummary> Graphs { get; init; }
        = Array.Empty<AgentOrchestrationGraphSummary>();
}

public sealed record AgentOrchestrationRunPageDto
{
    public string? WorkspaceId { get; init; }
    public string? GraphId { get; init; }
    public AgentOrchestrationRunStatus? Status { get; init; }
    public int Offset { get; init; }
    public int Count { get; init; }
    public bool HasMore { get; init; }
    public IReadOnlyList<AgentOrchestrationRunSummary> Runs { get; init; }
        = Array.Empty<AgentOrchestrationRunSummary>();
}

public sealed record AgentOrchestrationRevisionPageDto
{
    public required string GraphId { get; init; }
    public IReadOnlyList<AgentOrchestrationRevisionSummary> Revisions { get; init; }
        = Array.Empty<AgentOrchestrationRevisionSummary>();
    public int Count { get; init; }
}

public sealed record AgentOrchestrationEventPageDto
{
    public required string RunId { get; init; }
    public long AfterSequence { get; init; }
    public long NextSequence { get; init; }
    public long HeadSequence { get; init; }
    public bool HasMore { get; init; }
    public IReadOnlyList<AgentOrchestrationRunEvent> Events { get; init; }
        = Array.Empty<AgentOrchestrationRunEvent>();
}
