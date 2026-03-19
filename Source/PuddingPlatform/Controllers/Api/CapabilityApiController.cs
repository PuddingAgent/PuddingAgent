using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>平台能力管理 API。</summary>
[ApiController]
[Route("api/capabilities")]
public class CapabilityApiController(PlatformDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CapabilityDto>>> List([FromQuery] bool? enabledOnly, CancellationToken ct)
    {
        var query = db.Capabilities.AsNoTracking();
        if (enabledOnly == true) query = query.Where(x => x.IsEnabled);

        var list = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        return Ok(list.Select(MapToDto).ToList());
    }

    [HttpGet("{capabilityId}")]
    public async Task<ActionResult<CapabilityDto>> Get(string capabilityId, CancellationToken ct)
    {
        var entity = await db.Capabilities.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CapabilityId == capabilityId, ct);
        return entity is null ? NotFound() : Ok(MapToDto(entity));
    }

    [HttpPost]
    public async Task<ActionResult<CapabilityDto>> Create([FromBody] UpsertCapabilityRequest req, CancellationToken ct)
    {
        if (await db.Capabilities.AnyAsync(x => x.CapabilityId == req.CapabilityId, ct))
            return Conflict(new { error = $"CapabilityId '{req.CapabilityId}' 已存在" });

        var entity = BuildEntity(req);
        db.Capabilities.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { capabilityId = entity.CapabilityId }, MapToDto(entity));
    }

    [HttpPut("{capabilityId}")]
    public async Task<ActionResult<CapabilityDto>> Update(
        string capabilityId,
        [FromBody] UpsertCapabilityRequest req,
        CancellationToken ct)
    {
        var entity = await db.Capabilities.FirstOrDefaultAsync(x => x.CapabilityId == capabilityId, ct);
        if (entity is null) return NotFound();

        entity.Name = req.Name;
        entity.Description = req.Description;
        entity.ToolName = req.ToolName;
        entity.ToolDescription = req.ToolDescription;
        entity.ToolParametersJson = req.ToolParametersJson;
        entity.RequiresShellExecution = req.RequiresShellExecution;
        entity.RequiresFileWrite = req.RequiresFileWrite;
        entity.RequiresNetworkAccess = req.RequiresNetworkAccess;
        entity.IsEnabled = req.IsEnabled;
        entity.SortOrder = req.SortOrder;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(MapToDto(entity));
    }

    [HttpDelete("{capabilityId}")]
    public async Task<IActionResult> Delete(string capabilityId, CancellationToken ct)
    {
        var entity = await db.Capabilities.FirstOrDefaultAsync(x => x.CapabilityId == capabilityId, ct);
        if (entity is null) return NotFound();

        db.Capabilities.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static CapabilityEntity BuildEntity(UpsertCapabilityRequest req) => new()
    {
        CapabilityId = req.CapabilityId,
        Name = req.Name,
        Description = req.Description,
        ToolName = req.ToolName,
        ToolDescription = req.ToolDescription,
        ToolParametersJson = req.ToolParametersJson,
        RequiresShellExecution = req.RequiresShellExecution,
        RequiresFileWrite = req.RequiresFileWrite,
        RequiresNetworkAccess = req.RequiresNetworkAccess,
        IsEnabled = req.IsEnabled,
        SortOrder = req.SortOrder,
    };

    private static CapabilityDto MapToDto(CapabilityEntity x) => new(
        x.Id,
        x.CapabilityId,
        x.Name,
        x.Description,
        x.ToolName,
        x.ToolDescription,
        x.ToolParametersJson,
        x.RequiresShellExecution,
        x.RequiresFileWrite,
        x.RequiresNetworkAccess,
        x.IsEnabled,
        x.SortOrder,
        x.CreatedAt,
        x.UpdatedAt);
}
