using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Orchestration;
using PuddingPlatform.Services.Orchestration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Admin-only direct Run command used by the orchestration editor Run button.</summary>
[Authorize(Roles = "admin")]
[ApiController]
[Route("api/orchestrations/runs")]
public sealed class AgentOrchestrationRunCommandApiController(
    AgentOrchestrationManualRunService manualRunService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        AgentOrchestrationJson.CreateSerializerOptions();

    [HttpPost]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Start(
        [FromBody] AgentOrchestrationManualRunRequest request,
        CancellationToken ct = default)
    {
        var requestedBy = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "admin";
        var result = await manualRunService.StartAsync(request, requestedBy, ct);
        if (result.Kind == AgentOrchestrationManualRunResultKind.Success && result.Receipt is not null)
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
            AgentOrchestrationManualRunResultKind.NotFound => NotFound(error),
            AgentOrchestrationManualRunResultKind.Conflict => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
