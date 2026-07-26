using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

[Authorize]
[ApiController]
[Route("api/channel-providers")]
public sealed class ChannelProviderApiController(
    ChannelConfigurationFileService channels) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ChannelProviderDto>>> List(
        CancellationToken ct)
        => Ok(await channels.ListProvidersAsync(ct));

    [HttpPut("{providerId}")]
    public async Task<ActionResult<ChannelProviderDto>> Update(
        string providerId,
        [FromBody] UpdateChannelProviderRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await channels.UpdateProviderAsync(providerId, request, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
