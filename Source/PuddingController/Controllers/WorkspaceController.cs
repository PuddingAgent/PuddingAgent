using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>
/// Workspace 管理 API——查看 / 管理工作空间。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class WorkspaceController : ControllerBase
{
    private readonly InMemoryWorkspaceCatalog _catalog;

    public WorkspaceController(InMemoryWorkspaceCatalog catalog)
    {
        _catalog = catalog;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<WorkspaceDefinition>> List()
    {
        return Ok(_catalog.GetAll());
    }

    [HttpGet("{workspaceId}")]
    public ActionResult<WorkspaceDefinition> Get(string workspaceId)
    {
        var ws = _catalog.GetWorkspace(workspaceId);
        return ws is null ? NotFound() : Ok(ws);
    }

    /// <summary>创建或替换 Workspace。</summary>
    [HttpPut("{workspaceId}")]
    public ActionResult<WorkspaceDefinition> Upsert(string workspaceId, [FromBody] WorkspaceDefinition workspace)
    {
        if (workspaceId != workspace.WorkspaceId)
            return BadRequest("WorkspaceId in URL must match body.");

        _catalog.Upsert(workspace);
        return Ok(workspace);
    }

    /// <summary>删除 Workspace（不可删除内置）。</summary>
    [HttpDelete("{workspaceId}")]
    public ActionResult Delete(string workspaceId)
    {
        if (workspaceId == "default")
            return BadRequest("Cannot delete the default workspace.");

        var removed = _catalog.Remove(workspaceId);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>重新加载所有 Workspace（恢复出厂预设）。</summary>
    [HttpPost("reload")]
    public async Task<ActionResult> Reload(CancellationToken ct)
    {
        await _catalog.ReloadAsync(ct);
        return Ok(new { status = "reloaded", count = _catalog.GetAll().Count });
    }
}

