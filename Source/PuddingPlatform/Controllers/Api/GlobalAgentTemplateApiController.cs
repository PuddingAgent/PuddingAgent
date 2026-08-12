using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 全局系统 Agent 模板管理 API。模板配置以 data/agent-templates/{templateId}/ 为唯一保存主源。
/// </summary>
[ApiController]
[Route("api/global-agent-templates")]
public class GlobalAgentTemplateApiController(
    AgentTemplateFileService templates,
    ILogger<GlobalAgentTemplateApiController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<GlobalAgentTemplateDto>>> List(
        [FromQuery] bool? enabledOnly, CancellationToken ct)
    {
        var list = await templates.ListTemplatesAsync(enabledOnly, ct);
        return Ok(list);
    }

    [HttpGet("presets")]
    public async Task<ActionResult<List<GlobalAgentTemplateDto>>> ListPresets(CancellationToken ct)
    {
        var list = await templates.ListPresetTemplatesAsync(ct);
        return Ok(list);
    }

    [HttpPost("presets/{templateId}/import")]
    public async Task<ActionResult<GlobalAgentTemplateDto>> ImportPreset(string templateId, CancellationToken ct)
    {
        try
        {
            var imported = await templates.ImportPresetTemplateAsync(templateId, ct);
            return CreatedAtAction(nameof(Get), new { templateId = imported.TemplateId }, imported);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[GlobalTemplateApi] ImportPreset rejected template={TemplateId}", templateId);
            return Conflict(new { error = "模板导入冲突，请检查模板状态后重试。" });
        }
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
            logger.LogWarning(ex, "[GlobalTemplateApi] Create rejected");
            return Conflict(new { error = "模板创建冲突，请检查模板 ID 是否已存在。" });
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
            logger.LogWarning(ex, "[GlobalTemplateApi] Delete rejected template={TemplateId}", templateId);
            return BadRequest(new { error = "模板删除失败。" });
        }
    }
}
