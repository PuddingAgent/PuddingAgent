using Microsoft.AspNetCore.Mvc;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Tool Catalog API。
/// 后台 Tool 管理和 Agent 模板授权应从运行时注册表发现 Tool，而不是维护独立硬编码清单。
/// </summary>
[ApiController]
[Route("api/tools")]
public sealed class ToolCatalogApiController(
    IPuddingToolCatalogService catalog,
    IToolPermissionPolicyService toolPermissionPolicy) : ControllerBase
{
    [HttpGet]
    public ActionResult<List<ToolCatalogItemDto>> List([FromQuery] bool? enabledByDefaultOnly = null)
    {
        var tools = catalog.ListTools(enabledByDefaultOnly == true)
            .Select(Map)
            .ToList();

        return Ok(tools);
    }

    [HttpGet("{toolId}")]
    public ActionResult<ToolCatalogItemDto> Get(string toolId)
    {
        var descriptor = catalog.ListTools()
            .FirstOrDefault(t => t.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase));

        return descriptor is null
            ? NotFound()
            : Ok(Map(descriptor));
    }

    private ToolCatalogItemDto Map(ToolDescriptor descriptor)
    {
        var decision = toolPermissionPolicy.Classify(descriptor);
        return new ToolCatalogItemDto(
        ToolId: descriptor.ToolId,
        Name: descriptor.Name,
        Description: descriptor.Description,
        Category: descriptor.Category.ToString(),
        PermissionLevel: descriptor.PermissionLevel.ToString(),
        Safety: descriptor.Safety.ToString(),
        PermissionTier: decision.Tier.ToString(),
        RequiresRuntimeAuthorization: decision.RequiresRuntimeAuthorization,
        RequiresShellExecution: decision.RequiresShellExecution,
        RequiresFileWrite: decision.RequiresFileWrite,
        RequiresNetworkAccess: decision.RequiresNetworkAccess,
        Parameters: descriptor.Parameters,
        IsEnabledByDefault: descriptor.IsEnabledByDefault,
        SourceKind: descriptor.SourceKind,
        SourceId: descriptor.SourceId,
        RuntimeStatus: descriptor.RuntimeStatus,
        SortOrder: descriptor.SortOrder);
    }
}

public sealed record ToolCatalogItemDto(
    string ToolId,
    string Name,
    string Description,
    string Category,
    string PermissionLevel,
    string Safety,
    string PermissionTier,
    bool RequiresRuntimeAuthorization,
    bool RequiresShellExecution,
    bool RequiresFileWrite,
    bool RequiresNetworkAccess,
    ToolParameterSchema Parameters,
    bool IsEnabledByDefault,
    string SourceKind,
    string? SourceId,
    string RuntimeStatus,
    int SortOrder);
