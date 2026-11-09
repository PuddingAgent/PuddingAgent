using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pudding.Api.Contracts;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/workspaces/{workspaceId}/agents/{agentId}/configuration")]
public sealed class AgentConfigurationApiController(AgentConfigurationApiFactory factory) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(string workspaceId, string agentId, CancellationToken ct) => Execute(workspaceId, agentId,
        (api, agent) => api.GetAsync(agent, ct));

    [HttpPatch]
    public Task<IActionResult> Update(string workspaceId, string agentId, [FromBody] ConfigurationUpdateRequest request, CancellationToken ct) =>
        Execute(workspaceId, agentId, (api, agent) => api.UpdateAsync(agent, request.Patch, request.Options, ct));

    [HttpGet("changes")]
    public Task<IActionResult> Changes(string workspaceId, string agentId, long afterSequence = 0, int limit = 100, CancellationToken ct = default) =>
        Execute(workspaceId, agentId, (api, agent) => api.ReadChangesAsync(agent, afterSequence, limit, ct));

    private async Task<IActionResult> Execute<T>(string workspaceId, string agentId, Func<IAgentConfigurationApi, AgentRef, Task<T>> action)
    {
        try
        {
            var agent = new AgentRef(workspaceId, agentId);
            return Ok(await action(factory.ForManagementUser(User, agent), agent));
        }
        catch (ConfigurationApiException ex)
        {
            var status = ex.Code switch
            {
                "permission.denied" => 403,
                "resource.not_found" => 404,
                "revision.conflict" or "operation.conflict" or "resource.limit" => 409,
                "request.invalid" or "cursor.invalid" => 400,
                _ => 503
            };
            return StatusCode(status, new ConfigurationApiError(ex.Code, ex.Message, ex.EffectOutcome));
        }
    }
}
