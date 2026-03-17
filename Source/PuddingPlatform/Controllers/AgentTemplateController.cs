using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers;

/// <summary>Agent 模板列表与详情页。</summary>
public class AgentTemplateController : Controller
{
    private readonly PlatformApiClient _api;

    public AgentTemplateController(PlatformApiClient api) => _api = api;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var templates = await _api.GetAgentTemplatesAsync(ct);
        return View(templates);
    }

    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        var template = await _api.GetAgentTemplateAsync(id, ct);
        if (template is null) return NotFound();
        return View(template);
    }
}
