using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Workspace 级 Agent 模板管理 API（ADR-036：已废弃，工作区覆盖通过 Agent 实例配置实现）。
/// 保留接口返回空列表或 404，避免 Admin SPA 报错。
/// </summary>
[ApiController]
[Route("api/workspace-agent-templates")]
public class WorkspaceAgentTemplateApiController : ControllerBase
{
    [HttpGet]
    public ActionResult<List<WorkspaceAgentTemplateDto>> List(
        [FromQuery] string? workspaceId, [FromQuery] bool? enabledOnly)
    {
        // ADR-036：工作区级模板已废弃，全局模板通过 api/global-agent-templates 管理
        return Ok(new List<WorkspaceAgentTemplateDto>());
    }

    [HttpGet("{id:int}")]
    public ActionResult<WorkspaceAgentTemplateDto> GetById(int id)
    {
        return NotFound();
    }

    [HttpGet("{workspaceId}/{templateId}")]
    public ActionResult<WorkspaceAgentTemplateDto> GetByKey(
        string workspaceId, string templateId)
    {
        return NotFound();
    }

    [HttpPost]
    public ActionResult<WorkspaceAgentTemplateDto> Create(
        [FromBody] UpsertWorkspaceAgentTemplateRequest req)
    {
        return NotFound(new { error = "此 API 已废弃（ADR-036），工作区 Agent 配置通过 api/workspaces/{workspaceId}/agents 管理" });
    }

    [HttpPut("{id:int}")]
    public ActionResult<WorkspaceAgentTemplateDto> Update(
        int id, [FromBody] UpsertWorkspaceAgentTemplateRequest req)
    {
        return NotFound();
    }

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id)
    {
        return NotFound();
    }
}
