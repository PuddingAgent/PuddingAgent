using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers;

/// <summary>Session 查看控制器。</summary>
public class SessionController : Controller
{
    private readonly PlatformApiClient _api;

    public SessionController(PlatformApiClient api) => _api = api;

    public async Task<IActionResult> Index(string? workspaceId, CancellationToken ct)
    {
        var sessions = await _api.GetSessionsAsync(workspaceId, ct);
        ViewBag.WorkspaceId = workspaceId;
        return View(sessions);
    }

    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        var session = await _api.GetSessionAsync(id, ct);
        if (session is null) return NotFound();
        return View(session);
    }
}
