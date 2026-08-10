using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Orchestration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Editor-only layout API. Layout state is isolated from executable revisions and run facts;
/// only administrators may advance its independent CAS revision.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orchestrations/graphs/{graphId}/layout")]
public sealed class AgentOrchestrationLayoutApiController(IAgentOrchestrationStore store) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        AgentOrchestrationJson.CreateSerializerOptions();

    [HttpGet]
    public async Task<ActionResult<AgentOrchestrationGraphLayout>> Get(
        string graphId,
        [FromQuery] string? baseRevisionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseRevisionId))
        {
            return BadRequest(new
            {
                code = "orchestration.layout_base_revision_required",
                message = "baseRevisionId is required."
            });
        }

        var layout = await store.GetLayoutAsync(graphId, baseRevisionId, ct);
        return layout is null
            ? NotFound(new { code = "orchestration.layout_not_found", graphId, baseRevisionId })
            : new JsonResult(layout, JsonOptions);
    }

    [Authorize(Roles = "admin")]
    [HttpPut]
    public async Task<ActionResult<AgentOrchestrationGraphLayout>> Put(
        string graphId,
        [FromBody] AgentOrchestrationLayoutWriteRequest request,
        CancellationToken ct = default)
    {
        if (!string.Equals(graphId?.Trim(), request.Layout?.GraphId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                code = "orchestration.layout_route_graph_mismatch",
                message = "Route graphId must match layout.graphId."
            });
        }

        var result = await store.SaveLayoutAsync(request, ct);
        if (result.Success)
            return new JsonResult(result.Value, JsonOptions);

        var payload = new
        {
            code = result.ErrorCode ?? "orchestration.layout_write_failed",
            message = result.ErrorMessage ?? "Layout write failed.",
            currentLayoutRevision = result.CurrentVersion
        };
        return result.Status switch
        {
            AgentOrchestrationStoreStatus.NotFound => NotFound(payload),
            AgentOrchestrationStoreStatus.Conflict => Conflict(payload),
            _ => BadRequest(payload)
        };
    }
}
