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
}
