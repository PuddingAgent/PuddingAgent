using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pudding.Api.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatform.Controllers.Api;

/// <summary>PA-06 typed model policy read/write for the authenticated management user (no body-supplied actor).</summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/workspaces/{workspaceId}/agents/{agentId}/model-policy")]
public sealed class AgentModelPolicyController(AgentModelPolicyApiFactory factory) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(string workspaceId, string agentId, CancellationToken ct) => Execute(async api =>
        await api.GetEffectiveAsync(new AgentRef(workspaceId, agentId), ct));

    [HttpPatch]
    public Task<IActionResult> Update(string workspaceId, string agentId, [FromBody] UpdateAgentModelPolicy request,
        CancellationToken ct) => Execute(async api =>
        await api.UpdateAsync(request with { Agent = new AgentRef(workspaceId, agentId) }, ct));

    private async Task<IActionResult> Execute<T>(Func<IAgentModelPolicyApi, Task<T>> action)
    {
        try
        {
            return Ok(await action(factory.ForManagementUser(User)));
        }
        catch (AgentModelPolicyException ex)
        {
            var status = ex.Code switch
            {
                ModelErrorCodes.ScopeForbidden => 403,
                ModelErrorCodes.CapabilityMissing or ModelErrorCodes.PolicyMissing => 404,
                ModelErrorCodes.ConfigRevisionConflict => 409,
                ModelErrorCodes.RequestInvalid or ModelErrorCodes.FieldUnknown => 400,
                _ => 503,
            };
            return StatusCode(status, new { code = ex.Code, message = ex.Message });
        }
    }
}
