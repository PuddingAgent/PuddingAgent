using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "admin")]
[Route("api/workspaces/{workspace}/agents/{agent}/channel-trust/{channel}")]
public sealed class ChannelDevelopmentTrustController(ChannelDevelopmentTrust trust, IWorkspaceAgentCatalog agents) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Read(string workspace, string agent, string channel, CancellationToken ct) =>
        await ExistsAsync(workspace, agent, channel, ct) ? Ok(await trust.ReadAsync(workspace, agent, channel, ct)) : NotFound();

    [HttpPut]
    public async Task<IActionResult> Save(string workspace, string agent, string channel,
        [FromBody] ChannelTrustSettings settings, CancellationToken ct)
    {
        if (!await ExistsAsync(workspace, agent, channel, ct)) return NotFound();
        try { await trust.SaveAsync(workspace, agent, channel, settings, ct); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        return NoContent();
    }

    private async Task<bool> ExistsAsync(string workspace, string agent, string channel, CancellationToken ct)
    {
        var owners = await agents.ListAgentsAsync(workspace, ct);
        return owners.Any(owner => owner.AgentId == agent && (channel is ChannelDevelopmentTrust.Web or ChannelDevelopmentTrust.DingTalk ||
            owner.ChannelIds?.Contains(channel, StringComparer.Ordinal) == true));
    }
}
