using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 系统预置 Agent 头像只读 API（ADR-034）。
/// 头像由 avatars.json 种子产生，服务端统一管理。
/// </summary>
[ApiController]
[Route("api/agent-avatars")]
public class AgentAvatarApiController(
    PlatformDbContext db,
    AgentAvatarCatalog catalog) : ControllerBase
{
    /// <summary>获取头像列表</summary>
    /// <param name="enabledOnly">为 true 时只返回启用头像</param>
    [HttpGet]
    public async Task<ActionResult<List<AgentAvatarDto>>> List(
        [FromQuery] bool enabledOnly = true, CancellationToken ct = default)
    {
        var query = db.AgentAvatars.AsNoTracking();
        if (enabledOnly)
            query = query.Where(a => a.IsEnabled);

        var list = await query
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);

        return Ok(list.Select(MapToDto).ToList());
    }

    /// <summary>按 AvatarId 获取单个头像</summary>
    [HttpGet("{avatarId}")]
    public async Task<ActionResult<AgentAvatarDto>> Get(string avatarId, CancellationToken ct = default)
    {
        var entity = await db.AgentAvatars
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AvatarId == avatarId, ct);

        return entity is null ? NotFound() : Ok(MapToDto(entity));
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static AgentAvatarDto MapToDto(AgentAvatarEntity e) => new(
        e.AvatarId,
        e.Name,
        e.UrlPath,
        e.Personality,
        e.HairColor,
        e.Expression,
        SafeParseStringList(e.VisualTraitsJson),
        e.RecommendedUse,
        e.IsBuiltIn,
        e.IsEnabled,
        e.SortOrder
    );

    private static List<string> SafeParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
