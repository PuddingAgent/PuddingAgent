using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers;

/// <summary>Workspace 管理面板。</summary>
public class WorkspaceController : Controller
{
    private readonly PlatformApiClient _api;

    public WorkspaceController(PlatformApiClient api) => _api = api;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var workspaces = await _api.GetWorkspacesAsync(ct);
        return View(workspaces);
    }

    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        var ws = await _api.GetWorkspaceAsync(id, ct);
        if (ws is null) return NotFound();
        return View(ws);
    }
}
