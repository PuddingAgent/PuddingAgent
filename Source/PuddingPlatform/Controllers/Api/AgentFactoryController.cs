using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pudding.Api.Contracts;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// PA-02 I2 management factory: creation materializes an Agent instance (no inference, no Work, no charge); archive sets
/// the terminal manifest flags with a revision fence.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/workspaces/{workspaceId}/agent-instances")]
public sealed class AgentFactoryController(AgentFactoryApiFactory factory) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(string workspaceId, [FromBody] CreateAgentRequest request,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await factory.ForManagementUser(User)
                .CreateAsync(request with { WorkspaceId = workspaceId }, ct));
        }
        catch (AgentDirectoryException ex) { return Error(ex); }
    }

    [HttpPost("{agentId}/archive")]
    [HttpPost("~/api/workspaces/{workspaceId}/agents/{agentId}/archive")]
    public async Task<IActionResult> Archive(string workspaceId, string agentId, [FromBody] ArchiveAgentRequest request,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await factory.ForManagementUser(User)
                .ArchiveAsync(request with { Agent = new AgentRef(workspaceId, agentId) }, ct));
        }
        catch (AgentDirectoryException ex) { return Error(ex); }
    }

    private IActionResult Error(AgentDirectoryException ex) => StatusCode(ex.Code switch
    {
        "agent.forbidden" => 403,
        "agent.not_found" => 404,
        "agent.request_invalid" or "agent.binding_invalid" => 400,
        _ => 503,
    }, new { code = ex.Code, message = ex.Message });
}
