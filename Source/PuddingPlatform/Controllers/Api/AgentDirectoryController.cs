using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// PA-02 I0 borrowed directory: authenticated management callers enumerate/read Agent instances without fabricating a
/// Work/root budget. Read-only; never starts inference or charges.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/workspaces/{workspaceId}/agent-instances")]
public sealed class AgentDirectoryController(AgentDirectoryApiFactory factory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string workspaceId, [FromQuery] string? cursor = null,
        [FromQuery] int maxCount = 50, CancellationToken ct = default)
    {
        try
        {
            return Ok(await factory.ForManagementUser(User)
                .ListAsync(new AgentListQuery(workspaceId, maxCount, cursor), ct));
        }
        catch (AgentDirectoryException ex) { return Error(ex); }
    }

    [HttpGet("{agentId}")]
    public async Task<IActionResult> Get(string workspaceId, string agentId, CancellationToken ct = default)
    {
        try
        {
            var entry = await factory.ForManagementUser(User).GetAsync(new AgentRef(workspaceId, agentId), ct);
            return entry is null ? NotFound() : Ok(entry);
        }
        catch (AgentDirectoryException ex) { return Error(ex); }
    }

    private IActionResult Error(AgentDirectoryException ex) => StatusCode(ex.Code switch
    {
        AgentErrorCodes.Forbidden => 403,
        AgentErrorCodes.NotFound => 404,
        AgentErrorCodes.RequestInvalid or AgentErrorCodes.BindingInvalid => 400,
        _ => 503,
    }, new { code = ex.Code, message = ex.Message });
}
