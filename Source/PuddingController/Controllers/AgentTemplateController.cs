using Microsoft.AspNetCore.Mvc;
using PuddingAgent;
using PuddingCode.Platform;

namespace PuddingController.Controllers;

/// <summary>Agent 模板查询 API。</summary>
[ApiController]
[Route("api/[controller]")]
public class AgentTemplateController : ControllerBase
{
    /// <summary>列出所有内置 Agent 模板。</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<AgentTemplateDefinition>> List()
    {
        return Ok(BuiltInAgentTemplates.GetAll());
    }

    /// <summary>查询指定模板。</summary>
    [HttpGet("{templateId}")]
    public ActionResult<AgentTemplateDefinition> Get(string templateId)
    {
        var template = BuiltInAgentTemplates.FindById(templateId);
        return template is null ? NotFound() : Ok(template);
    }
}
