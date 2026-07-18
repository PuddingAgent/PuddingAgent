using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 系统预置 Agent 头像只读 API（ADR-034 revised）。
/// 数据来自内存目录，PNG 由 wwwroot 静态资源提供。
/// </summary>
[ApiController]
[Route("api/agent-avatars")]
public class AgentAvatarApiController(
    IAgentAvatarCatalog catalog) : ControllerBase
{
    /// <summary>获取头像列表</summary>
    [HttpGet]
    public ActionResult<List<AgentAvatarDto>> List(CancellationToken ct = default)
    {
        var list = catalog.List();
        var defaultId = catalog.GetDefault().AvatarId;
        return Ok(list.Select(d => MapToDto(d, d.AvatarId == defaultId)).ToList());
    }

    /// <summary>按 AvatarId 获取单个头像</summary>
    [HttpGet("{avatarId}")]
    public ActionResult<AgentAvatarDto> Get(string avatarId, CancellationToken ct = default)
    {
        var def = catalog.Find(avatarId);
        if (def is null) return NotFound();

        var isDefault = def.AvatarId == catalog.GetDefault().AvatarId;
        return Ok(MapToDto(def, isDefault));
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static AgentAvatarDto MapToDto(AgentAvatarDefinition d, bool isDefault) => new(
        d.AvatarId,
        d.Name,
        d.UrlPath,
        d.Personality,
        HairColor: null,
        Expression: null,
        VisualTraits: [],
        d.RecommendedUse,
        IsBuiltIn: true,
        d.IsEnabled,
        d.SortOrder,
        IsDefault: isDefault
    );
}
