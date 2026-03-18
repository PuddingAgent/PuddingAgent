using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Workspace JSON API（供 Admin SPA 调用）。</summary>
[ApiController]
[Route("api/workspaces")]
public class WorkspaceApiController : ControllerBase
{
    private readonly PlatformApiClient _api;

    public WorkspaceApiController(PlatformApiClient api) => _api = api;

    /// <summary>GET /api/workspaces</summary>
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceDefinition>>> List(CancellationToken ct)
    {
        var list = await _api.GetWorkspacesAsync(ct);
        return Ok(list);
    }

    /// <summary>GET /api/workspaces/{id}</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<WorkspaceDefinition>> Get(string id, CancellationToken ct)
    {
        var ws = await _api.GetWorkspaceAsync(id, ct);
        return ws is null ? NotFound() : Ok(ws);
    }

    /// <summary>POST /api/workspaces</summary>
    [HttpPost]
    public async Task<ActionResult<WorkspaceDefinition>> Create(
        [FromBody] WorkspaceDefinition workspace, CancellationToken ct)
    {
        var result = await _api.UpsertWorkspaceAsync(workspace, ct);
        return result is null
            ? StatusCode(502, "Controller 无响应")
            : Ok(result);
    }

    /// <summary>PUT /api/workspaces/{id}</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<WorkspaceDefinition>> Update(
        string id, [FromBody] WorkspaceDefinition workspace, CancellationToken ct)
    {
        if (id != workspace.WorkspaceId)
            return BadRequest("WorkspaceId 与 URL 不一致");
        var result = await _api.UpsertWorkspaceAsync(workspace, ct);
        return result is null ? StatusCode(502, "Controller 无响应") : Ok(result);
    }

    /// <summary>DELETE /api/workspaces/{id}</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var ok = await _api.DeleteWorkspaceAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>POST /api/workspaces/{id}/freeze</summary>
    [HttpPost("{id}/freeze")]
    public async Task<IActionResult> Freeze(string id, CancellationToken ct)
    {
        await _api.FreezeWorkspaceAsync(id, ct);
        return Ok();
    }

    /// <summary>POST /api/workspaces/{id}/unfreeze</summary>
    [HttpPost("{id}/unfreeze")]
    public async Task<IActionResult> Unfreeze(string id, CancellationToken ct)
    {
        await _api.UnfreezeWorkspaceAsync(id, ct);
        return Ok();
    }
}
