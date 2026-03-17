using Microsoft.AspNetCore.Mvc;
using PuddingAgent;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>Agent 模板查询与管理 API。</summary>
[ApiController]
[Route("api/[controller]")]
public class AgentTemplateController : ControllerBase
{
    private readonly AgentTemplateRegistry _registry;

    public AgentTemplateController(AgentTemplateRegistry registry)
        => _registry = registry;

    /// <summary>列出所有 Agent 模板（内置 + 用户自定义）。</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<AgentTemplateDefinition>> List()
        => Ok(_registry.GetAll());

    /// <summary>查询指定模板。</summary>
    [HttpGet("{templateId}")]
    public ActionResult<AgentTemplateDefinition> Get(string templateId)
    {
        var template = _registry.FindById(templateId);
        return template is null ? NotFound() : Ok(template);
    }

    /// <summary>注册或覆盖用户自定义模板（不可覆盖内置模板）。</summary>
    [HttpPut("{templateId}")]
    public ActionResult<AgentTemplateDefinition> Register(
        string templateId,
        [FromBody] AgentTemplateDefinition template)
    {
        if (templateId != template.TemplateId)
            return BadRequest("TemplateId in URL must match body.");

        if (!_registry.Register(template, out var error))
            return Conflict(new { error });

        return Ok(template);
    }

    /// <summary>删除用户自定义模板（内置模板不可删除）。</summary>
    [HttpDelete("{templateId}")]
    public ActionResult Delete(string templateId)
    {
        if (!_registry.Remove(templateId, out var error))
        {
            if (error is not null)
                return BadRequest(new { error });
            return NotFound();
        }
        return NoContent();
    }
}

