using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Security;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// SKILL Hub 中央技能库 API（设计契约 §5.2 冻结的 15 个端点）。
/// 与 /api/skill-packages（Agent 模板选包用的二进制附件）是两条独立链路。
/// 进程内直连：直接注入 ISkillHubService（Scoped，与 scoped PlatformDbContext 生命周期一致），
/// 不再按请求手工 new 服务实例。
/// </summary>
[Authorize(Policy = SkillHubApiPolicyNames.SkillHubClient)]
[ApiController]
[Route("api/skill-hub")]
public class SkillHubController(ISkillHubService hubService) : ControllerBase
{
    // GET /api/skill-hub/stats — 概览指标
    [HttpGet("stats")]
    public async Task<ActionResult<HubSkillStatsDto>> Stats(CancellationToken ct)
    {
        return Ok(await hubService.GetStatsAsync(ct));
    }

    // GET /api/skill-hub/skills?query=&tag=&status=&page=&pageSize= — 列表/搜索
    [HttpGet("skills")]
    public async Task<ActionResult<List<HubSkillSummaryDto>>> ListSkills(
        [FromQuery] string? query,
        [FromQuery] string? tag,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        return Ok(await hubService.ListSkillsAsync(query, tag, status, page, pageSize, ct));
    }

    // GET /api/skill-hub/skills/{skillId} — 详情（版本列表 + 最近安装）
    [HttpGet("skills/{skillId}")]
    public async Task<ActionResult<HubSkillDetailDto>> GetSkill(string skillId, CancellationToken ct)
    {
        var detail = await hubService.GetSkillAsync(skillId, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    // GET /api/skill-hub/skills/{skillId}/versions — 版本列表
    [HttpGet("skills/{skillId}/versions")]
    public async Task<ActionResult<List<HubSkillVersionDto>>> ListVersions(string skillId, CancellationToken ct)
    {
        var versions = await hubService.ListVersionsAsync(skillId, ct);
        return versions is null ? NotFound() : Ok(versions);
    }

    // GET /api/skill-hub/skills/{skillId}/versions/{version} — 单版本全文（含 SkillMarkdown）
    [HttpGet("skills/{skillId}/versions/{version}")]
    public async Task<ActionResult<HubSkillVersionContentDto>> GetVersion(
        string skillId, string version, CancellationToken ct)
    {
        var content = await hubService.GetVersionAsync(skillId, version, ct);
        return content is null ? NotFound() : Ok(content);
    }

    // GET /api/skill-hub/skills/{skillId}/lineage — 单技能血缘（EVO MAP 子图）
    [HttpGet("skills/{skillId}/lineage")]
    public async Task<ActionResult<EvoMapDto>> GetSkillLineage(string skillId, CancellationToken ct)
    {
        var lineage = await hubService.GetSkillLineageAsync(skillId, ct);
        return lineage is null ? NotFound() : Ok(lineage);
    }

    // GET /api/skill-hub/lineage?skillIds=a,b,c&limit= — 全局/多技能 EVO MAP
    [HttpGet("lineage")]
    public async Task<ActionResult<EvoMapDto>> GetLineage(
        [FromQuery] string? skillIds,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        var idList = string.IsNullOrWhiteSpace(skillIds)
            ? null
            : skillIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return Ok(await hubService.GetLineageAsync(idList, limit, ct));
    }

    // POST /api/skill-hub/skills — 发布新技能
    [HttpPost("skills")]
    public async Task<ActionResult<HubSkillSummaryDto>> Publish(
        [FromBody] PublishHubSkillRequest req, CancellationToken ct)
    {
        var result = await hubService.PublishAsync(req, ct);
        if (result.IsOk) return CreatedAtAction(nameof(GetSkill), new { skillId = req.SkillId }, result.Value);
        return MapError(result.Status, result.Error);
    }

    // POST /api/skill-hub/skills/{skillId}/versions — 发布新版本 / 进化
    [HttpPost("skills/{skillId}/versions")]
    public async Task<ActionResult<HubSkillVersionDto>> PublishVersion(
        string skillId, [FromBody] PublishHubSkillRequest req, CancellationToken ct)
    {
        var result = await hubService.PublishVersionAsync(skillId, req, ct);
        if (result.IsOk) return CreatedAtAction(nameof(GetVersion), new { skillId, version = result.Value!.Version }, result.Value);
        return MapError(result.Status, result.Error);
    }

    // PATCH /api/skill-hub/skills/{skillId} — 改元数据 / 停用 / 退役
    [HttpPatch("skills/{skillId}")]
    public async Task<ActionResult<HubSkillSummaryDto>> UpdateMeta(
        string skillId, [FromBody] UpdateHubSkillMetaRequest req, CancellationToken ct)
    {
        var result = await hubService.UpdateMetaAsync(skillId, req, ct);
        return result.IsOk ? Ok(result.Value) : MapError(result.Status, result.Error);
    }

    // DELETE /api/skill-hub/skills/{skillId} — 软删（→ retired + 事件）
    [HttpDelete("skills/{skillId}")]
    public async Task<IActionResult> Retire(string skillId, CancellationToken ct)
    {
        var result = await hubService.RetireAsync(skillId, ct);
        return result.IsOk ? NoContent() : MapError(result.Status, result.Error);
    }

    // POST /api/skill-hub/installs — 登记安装（upsert）
    [HttpPost("installs")]
    public async Task<ActionResult<HubSkillInstallDto>> RegisterInstall(
        [FromBody] RegisterInstallRequest req, CancellationToken ct)
    {
        var result = await hubService.RegisterInstallAsync(req, ct);
        return result.IsOk ? Ok(result.Value) : MapError(result.Status, result.Error);
    }

    // GET /api/skill-hub/installs?agentInstanceId=&skillId=&page=&pageSize= — 安装台账
    [HttpGet("installs")]
    public async Task<ActionResult<List<HubSkillInstallDto>>> ListInstalls(
        [FromQuery] string? agentInstanceId,
        [FromQuery] string? skillId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        return Ok(await hubService.ListInstallsAsync(agentInstanceId, skillId, page, pageSize, ct));
    }

    // GET /api/skill-hub/events?skillId=&limit= — 审计事件
    [HttpGet("events")]
    public async Task<ActionResult<List<HubSkillEventDto>>> ListEvents(
        [FromQuery] string? skillId,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        return Ok(await hubService.ListEventsAsync(skillId, limit, ct));
    }

    // GET /api/skill-hub/updates?agentInstanceId= — 待更新清单
    [HttpGet("updates")]
    public async Task<ActionResult<List<HubSkillUpdateDto>>> ListUpdates(
        [FromQuery] string agentInstanceId, CancellationToken ct)
    {
        return Ok(await hubService.ListUpdatesAsync(agentInstanceId, ct));
    }

    // ── 辅助 ────────────────────────────────────────────────────────

    private ActionResult MapError(SkillHubStatus status, string? error) => status switch
    {
        SkillHubStatus.NotFound => NotFound(new { error }),
        SkillHubStatus.Conflict => Conflict(new { error }),
        SkillHubStatus.BadRequest => BadRequest(new { error }),
        _ => StatusCode(500, new { error }),
    };
}
