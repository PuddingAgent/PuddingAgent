using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pudding.Api.Contracts;
using PuddingPlatform.Services.AgentWork;

namespace PuddingPlatform.Controllers.Api;

/// <summary>PA-02 I2 Work Observe/Cancel for the authenticated management actor.</summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/workspaces/{workspaceId}/agents/{agentId}/work-control")]
public sealed class AgentWorkControlController(AgentWorkControlApiFactory factory) : ControllerBase
{
    [HttpGet("{operationId}")]
    public Task<IActionResult> Observe(string workspaceId, string agentId, string operationId,
        [FromQuery] long afterSequence = 0, [FromQuery] int maxCount = 100, CancellationToken ct = default)
        => Execute(workspaceId, agentId, async api =>
            Ok(await api.ObserveAsync(new ObserveWorkRequest(new(workspaceId, agentId), operationId, afterSequence,
                maxCount), ct)));

    [HttpPost("{operationId}/cancel")]
    public Task<IActionResult> Cancel(string workspaceId, string agentId, string operationId,
        [FromBody] CancelWorkRequest request, CancellationToken ct = default)
        => Execute(workspaceId, agentId, async api => Ok(await api.CancelAsync(
            request with { Agent = new(workspaceId, agentId), TargetOperationId = operationId }, ct)));

    private async Task<IActionResult> Execute(string workspaceId, string agentId,
        Func<IAgentWorkControlApi, Task<IActionResult>> action)
    {
        try
        {
            return await action(factory.ForManagementUser(User, new(workspaceId, agentId)));
        }
        catch (AgentWorkControlException ex)
        {
            return StatusCode(ex.Code switch
            {
                "permission.denied" => 403,
                "resource.not_found" => 404,
                "request.invalid" or "revision.conflict" => 400,
                _ => 503,
            }, new { code = ex.Code, message = ex.Message });
        }
    }
}
