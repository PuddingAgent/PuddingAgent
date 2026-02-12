using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingCode.Tools;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 平台 Tool 能力目录 API。
/// Tool 是否存在由运行时注册表决定；模板授权只保存授予关系，不再维护第二份 capability 清单。
/// </summary>
[ApiController]
[Route("api/capabilities")]
public class CapabilityApiController(
    IPuddingToolCatalogService toolCatalog,
    IToolPermissionPolicyService toolPermissionPolicy) : ControllerBase
{
    [HttpGet]
    public Task<ActionResult<List<CapabilityDto>>> List([FromQuery] bool? enabledOnly, CancellationToken ct)
        => Task.FromResult<ActionResult<List<CapabilityDto>>>(Ok(toolCatalog.ListTools()
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.ToolId, StringComparer.OrdinalIgnoreCase)
            .Select(MapToDto)
            .ToList()));

    [HttpGet("{capabilityId}")]
    public ActionResult<CapabilityDto> Get(string capabilityId, CancellationToken ct)
        => toolCatalog.ListTools()
            .FirstOrDefault(d => ToolIdToCapabilityId(d.ToolId).Equals(capabilityId, StringComparison.OrdinalIgnoreCase))
            is { } descriptor
            ? Ok(MapToDto(descriptor))
            : NotFound();

    [HttpPost]
    public ActionResult<CapabilityDto> Create([FromBody] UpsertCapabilityRequest req, CancellationToken ct)
        => StatusCode(StatusCodes.Status405MethodNotAllowed, new
        {
            error = "Tool capabilities are derived from the runtime tool registry and cannot be created through this API."
        });

    [HttpPut("{capabilityId}")]
    public ActionResult<CapabilityDto> Update(
        string capabilityId,
        [FromBody] UpsertCapabilityRequest req,
        CancellationToken ct)
        => StatusCode(StatusCodes.Status405MethodNotAllowed, new
        {
            error = "Tool capabilities are derived from the runtime tool registry and cannot be updated through this API."
        });

    [HttpDelete("{capabilityId}")]
    public IActionResult Delete(string capabilityId, CancellationToken ct)
        => StatusCode(StatusCodes.Status405MethodNotAllowed, new
        {
            error = "Tool capabilities are derived from the runtime tool registry and cannot be deleted through this API."
        });

    private CapabilityDto MapToDto(ToolDescriptor descriptor)
    {
        var decision = toolPermissionPolicy.Classify(descriptor);
        var now = DateTimeOffset.UtcNow;
        return new CapabilityDto(
            Id: StablePositiveId(descriptor.ToolId),
            CapabilityId: ToolIdToCapabilityId(descriptor.ToolId),
            Name: descriptor.Name,
            Description: descriptor.Description,
            ToolName: descriptor.ToolId,
            ToolDescription: descriptor.Description,
            ToolParametersJson: null,
            RequiresShellExecution: decision.RequiresShellExecution,
            RequiresFileWrite: decision.RequiresFileWrite,
            RequiresNetworkAccess: decision.RequiresNetworkAccess,
            IsEnabled: true,
            SortOrder: descriptor.SortOrder,
            SourceKind: descriptor.SourceKind,
            SourceId: descriptor.SourceId,
            RuntimeStatus: descriptor.RuntimeStatus,
            CreatedAt: now,
            UpdatedAt: now);
    }

    private static string ToolIdToCapabilityId(string toolId)
        => $"cap-{toolId.Trim().Replace('_', '-').ToLowerInvariant()}";

    private static int StablePositiveId(string value)
    {
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(value);
        return hash == int.MinValue ? int.MaxValue : Math.Abs(hash);
    }
}
