using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 全局系统 Agent 模板管理 API（文件式配置，存储在 data/agent-templates/{templateId}/）。
/// </summary>
[ApiController]
[Route("api/global-agent-templates")]
public class GlobalAgentTemplateApiController(
    AgentTemplateFileService fileService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<GlobalAgentTemplateDto>>> List(
        [FromQuery] bool? enabledOnly, CancellationToken ct)
    {
        var list = await fileService.ListTemplatesAsync(enabledOnly, ct);
        return Ok(list);
    }

    [HttpGet("{templateId}")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Get(string templateId, CancellationToken ct)
    {
        var t = await fileService.GetTemplateAsync(templateId, ct);
        if (t is null) return NotFound();
        return Ok(t);
    }

    [HttpPost]
    public async Task<ActionResult<GlobalAgentTemplateDto>> Create(
        [FromBody] UpsertGlobalAgentTemplateRequest req, CancellationToken ct)
    {
        try
        {
            var result = await fileService.CreateTemplateAsync(req, ct);
            return CreatedAtAction(nameof(Get), new { templateId = result.TemplateId }, result);
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
            var result = await fileService.UpdateTemplateAsync(templateId, req, ct);
            return Ok(result);
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
            await fileService.DeleteTemplateAsync(templateId, ct);
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
