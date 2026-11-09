using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pudding.Api.Contracts;
using PuddingPlatform.Services.AgentWork;

namespace PuddingPlatform.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/workspaces/{workspaceId}/agents/{agentId}/work")]
public sealed class AgentWorkApiController(AgentWorkApiFactory factory) : ControllerBase
{
    [HttpPost]
    public Task<IActionResult> Submit(string workspaceId, string agentId, [FromBody] AgentWorkRequest request, CancellationToken ct)
        => Execute(workspaceId, agentId, async (api, agent) => Ok(await api.SubmitAsync(agent, request, ct)));

    [HttpGet("{operationId}")]
    public Task<IActionResult> Get(string workspaceId, string agentId, string operationId, CancellationToken ct)
        => Execute(workspaceId, agentId, async (api, agent) => await api.GetAsync(agent, operationId, ct) is { } snapshot
            ? Ok(snapshot) : NoContent());

    private async Task<IActionResult> Execute(string workspaceId, string agentId, Func<IAgentWorkApi, AgentRef, Task<IActionResult>> action)
    {
        try
        {
            var agent = new AgentRef(workspaceId, agentId);
            return await action(factory.ForManagementUser(User, agent), agent);
        }
        catch (AgentWorkException ex)
        {
            var status = ex.Code switch
            {
                "permission.denied" => 403, "resource.not_found" => 404, "request.invalid" => 400,
                "agent.unavailable" or "agent.not_ready" or "operation.conflict" => 409, _ => 503
            };
            return StatusCode(status, new AgentWorkError(ex.Code, ex.Message, ex.EffectOutcome));
        }
    }
}
