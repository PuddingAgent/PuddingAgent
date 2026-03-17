using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers;

/// <summary>Workspace 管理面板。</summary>
public class WorkspaceController : Controller
{
    private readonly PlatformApiClient _api;
    private readonly WorkspaceBusinessService _business;

    public WorkspaceController(PlatformApiClient api, WorkspaceBusinessService business)
    {
        _api = api;
        _business = business;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var workspaces = await _api.GetWorkspacesAsync(ct);
        return View(workspaces);
    }

    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        var ws = await _api.GetWorkspaceAsync(id, ct);
        if (ws is null) return NotFound();
        var governance = await _business.GetGovernanceStatusAsync(id, ct);
        ViewData["Governance"] = governance;
        return View(ws);
    }

    [HttpPost]
    public async Task<IActionResult> Freeze(string id, CancellationToken ct)
    {
        await _api.FreezeWorkspaceAsync(id, ct);
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Unfreeze(string id, CancellationToken ct)
    {
        await _api.UnfreezeWorkspaceAsync(id, ct);
        return RedirectToAction(nameof(Detail), new { id });
    }
}
