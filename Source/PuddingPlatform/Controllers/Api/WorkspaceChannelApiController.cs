using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Workspace-scoped, file-backed channel instance management.</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/channels")]
public sealed class WorkspaceChannelApiController(
    ChannelConfigurationFileService channels,
    ILogger<WorkspaceChannelApiController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkspaceChannelDto>>> List(
        string workspaceId,
        CancellationToken ct)
        => Ok(await channels.ListWorkspaceChannelsAsync(workspaceId, ct));

    [HttpGet("{channelId}")]
    public async Task<ActionResult<WorkspaceChannelDto>> Get(
        string workspaceId,
        string channelId,
        CancellationToken ct)
    {
        var channel = await channels.GetWorkspaceChannelAsync(workspaceId, channelId, ct);
        return channel is null ? NotFound() : Ok(channel);
    }

    [HttpPost]
    public async Task<ActionResult<WorkspaceChannelDto>> Create(
        string workspaceId,
        [FromBody] UpsertWorkspaceChannelRequest request,
        CancellationToken ct)
    {
        try
        {
            var created = await channels.CreateWorkspaceChannelAsync(workspaceId, request, ct);
            return CreatedAtAction(
                nameof(Get),
                new { workspaceId, channelId = created.ChannelId },
                created);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "[WorkspaceChannelApi] Create rejected workspace={WorkspaceId}", workspaceId);
            return BadRequest(new { message = "渠道创建失败，请检查渠道配置。" });
        }
    }

    [HttpPut("{channelId}")]
    public async Task<ActionResult<WorkspaceChannelDto>> Update(
        string workspaceId,
        string channelId,
        [FromBody] UpsertWorkspaceChannelRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await channels.UpdateWorkspaceChannelAsync(
                workspaceId,
                channelId,
                request,
                ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            logger.LogWarning(ex, "[WorkspaceChannelApi] Update rejected workspace={WorkspaceId} channel={ChannelId}", workspaceId, channelId);
            return BadRequest(new { message = "渠道更新失败，请检查渠道配置。" });
        }
    }

    [HttpDelete("{channelId}")]
    public async Task<IActionResult> Delete(
        string workspaceId,
        string channelId,
        CancellationToken ct)
    {
        try
        {
            await channels.DeleteWorkspaceChannelAsync(workspaceId, channelId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
