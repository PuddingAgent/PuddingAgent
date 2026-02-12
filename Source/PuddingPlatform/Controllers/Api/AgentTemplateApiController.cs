using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>AgentTemplate 查询 JSON API（供 Admin SPA 调用）。</summary>
[ApiController]
[Route("api/agent-templates")]
public class AgentTemplateApiController : ControllerBase
{
    private readonly PlatformApiClient _api;

    public AgentTemplateApiController(PlatformApiClient api) => _api = api;

    /// <summary>GET /api/agent-templates</summary>
    [HttpGet]
    public async Task<ActionResult<List<AgentTemplateDefinition>>> List(CancellationToken ct)
    {
        var list = await _api.GetAgentTemplatesAsync(ct);
        return Ok(list);
    }

    /// <summary>GET /api/agent-templates/{templateId}</summary>
    [HttpGet("{templateId}")]
    public async Task<ActionResult<AgentTemplateDefinition>> Get(string templateId, CancellationToken ct)
    {
        var template = await _api.GetAgentTemplateAsync(templateId, ct);
        return template is null ? NotFound() : Ok(template);
    }
}
