using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Orchestration;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Admin-authenticated HTTP/API hook for debugging one explicit immutable graph Revision.
/// This is not a public anonymous webhook and never resolves a Graph Head implicitly.
/// </summary>
[Authorize(Roles = "admin")]
[ApiController]
[Route("api/orchestrations/hooks")]
public sealed class AgentOrchestrationHttpHookApiController(
    AgentOrchestrationHttpHookService hookService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        AgentOrchestrationJson.CreateSerializerOptions();

    [HttpPost("{graphId}/{triggerId}")]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Invoke(
        string graphId,
        string triggerId,
        [FromQuery] string revisionId,
        [FromBody] JsonElement body,
        CancellationToken ct = default)
    {
        AgentOrchestrationHttpHookInvokeRequest? request;
        try
        {
            request = body.Deserialize<AgentOrchestrationHttpHookInvokeRequest>(JsonOptions);
        }
        catch (JsonException)
        {
            request = null;
        }
        catch (NotSupportedException)
        {
            request = null;
        }
        if (request is null)
        {
            return BadRequest(new
            {
                code = "orchestration.http_hook_json_invalid",
                message = "Request body must contain sourceEventId and an optional payload."
            });
        }

        var requestedBy = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "admin";
        var result = await hookService.InvokeAsync(
            graphId,
            revisionId,
            triggerId,
            request,
            requestedBy,
            ct);
        if (result.Kind == AgentOrchestrationHttpHookResultKind.Success && result.Receipt is not null)
        {
            return new JsonResult(result.Receipt, JsonOptions)
            {
                StatusCode = result.Receipt.Created
                    ? StatusCodes.Status201Created
                    : StatusCodes.Status200OK
            };
        }

        var error = new { code = result.ErrorCode, message = result.ErrorMessage };
        return result.Kind switch
        {
            AgentOrchestrationHttpHookResultKind.NotFound => NotFound(error),
            AgentOrchestrationHttpHookResultKind.Conflict => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
