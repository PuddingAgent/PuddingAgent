using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 全局系统 Agent 模板管理 API。模板配置以 data/agent-templates/{templateId}/ 为唯一保存主源。
/// </summary>
[ApiController]
[Route("api/global-agent-templates")]
public class GlobalAgentTemplateApiController(AgentTemplateFileService templates) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<GlobalAgentTemplateDto>>> List(
        [FromQuery] bool? enabledOnly, CancellationToken ct)
    {
        var list = await templates.ListTemplatesAsync(enabledOnly, ct);
        return Ok(list);
    }

    [HttpGet("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Get(string templateId, CancellationToken ct)
    {
        var template = await templates.GetTemplateAsync(templateId, ct);
        return template is null ? NotFound() : Ok(template);
    }

    [HttpPost]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Create(
        [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        try
        {
            var created = await templates.CreateTemplateAsync(req, ct);
            return CreatedAtAction(nameof(Get), new { templateId = created.TemplateId }, created);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Update(
        string templateId, [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await templates.UpdateTemplateAsync(templateId, req, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{templateId}")]
    public async Task<IActionResult> Delete(string templateId, CancellationToken ct)
    {
        try
        {
            await templates.DeleteTemplateAsync(templateId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
