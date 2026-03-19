using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>LLM 资源池 — 服务商管理 API</summary>
[ApiController]
[Route("api/llm/providers")]
public class LlmProviderApiController(PlatformDbContext db) : ControllerBase
{
    // ── 查询所有 provider（附配额摘要）──────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<LlmProviderDto>>> List(CancellationToken ct)
    {
        var providers = await db.LlmProviders
            .AsNoTracking()
            .Include(p => p.Quota)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        return Ok(providers.Select(MapToDto).ToList());
    }

    // ── 查询单个 provider（含模型列表和配额）────────────────────
    [HttpGet("{providerId}")]
    public async Task<ActionResult<LlmProviderDetailDto>> Get(string providerId, CancellationToken ct)
    {
        var p = await db.LlmProviders
            .AsNoTracking()
            .Include(x => x.Models)
            .Include(x => x.Quota)
            .FirstOrDefaultAsync(x => x.ProviderId == providerId, ct);

        if (p is null) return NotFound();

        return Ok(MapToDetailDto(p));
    }

    // ── 创建 provider ────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<LlmProviderDto>> Create(
        [FromBody] UpsertLlmProviderRequest req, CancellationToken ct)
    {
        if (await db.LlmProviders.AnyAsync(p => p.ProviderId == req.ProviderId, ct))
            return Conflict(new { error = $"ProviderId '{req.ProviderId}' 已存在" });

        var entity = new LlmProviderEntity
        {
            ProviderId = req.ProviderId,
            Name = req.Name,
            Protocol = req.Protocol,
            BaseUrl = req.BaseUrl,
            ApiKey = req.ApiKey,
            Description = req.Description,
            IsEnabled = req.IsEnabled,
        };
        db.LlmProviders.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { providerId = entity.ProviderId }, MapToDto(entity));
    }

    // ── 更新 provider ────────────────────────────────────────────
    [HttpPut("{providerId}")]
    public async Task<ActionResult<LlmProviderDto>> Update(
        string providerId, [FromBody] UpsertLlmProviderRequest req, CancellationToken ct)
    {
        var entity = await db.LlmProviders.FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (entity is null) return NotFound();

        entity.Name = req.Name;
        entity.Protocol = req.Protocol;
        entity.BaseUrl = req.BaseUrl;
        if (req.ApiKey is not null) entity.ApiKey = req.ApiKey;
        entity.Description = req.Description;
        entity.IsEnabled = req.IsEnabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(MapToDto(entity));
    }

    // ── 删除 provider ────────────────────────────────────────────
    [HttpDelete("{providerId}")]
    public async Task<IActionResult> Delete(string providerId, CancellationToken ct)
    {
        var entity = await db.LlmProviders.FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (entity is null) return NotFound();

        db.LlmProviders.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── 配额管理 ─────────────────────────────────────────────────

    [HttpGet("{providerId}/quota")]
    public async Task<ActionResult<LlmProviderQuotaDto>> GetQuota(string providerId, CancellationToken ct)
    {
        var p = await db.LlmProviders
            .AsNoTracking()
            .Include(x => x.Quota)
            .FirstOrDefaultAsync(x => x.ProviderId == providerId, ct);
        if (p is null) return NotFound();

        return p.Quota is null
            ? Ok(new LlmProviderQuotaDto(null, null, 0, 0, false, null, null, DateTimeOffset.UtcNow))
            : Ok(MapQuotaToDto(p.Quota));
    }

    [HttpPut("{providerId}/quota")]
    public async Task<ActionResult<LlmProviderQuotaDto>> UpsertQuota(
        string providerId, [FromBody] UpdateQuotaRequest req, CancellationToken ct)
    {
        var p = await db.LlmProviders
            .Include(x => x.Quota)
            .FirstOrDefaultAsync(x => x.ProviderId == providerId, ct);
        if (p is null) return NotFound();

        if (p.Quota is null)
        {
            p.Quota = new LlmProviderQuotaEntity
            {
                DailyTokenLimit = req.DailyTokenLimit,
                MonthlyTokenLimit = req.MonthlyTokenLimit,
            };
        }
        else
        {
            p.Quota.DailyTokenLimit = req.DailyTokenLimit;
            p.Quota.MonthlyTokenLimit = req.MonthlyTokenLimit;
            p.Quota.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok(MapQuotaToDto(p.Quota));
    }

    [HttpPost("{providerId}/quota/reset-daily")]
    public async Task<IActionResult> ResetDailyQuota(string providerId, CancellationToken ct)
    {
        var quota = await db.LlmProviderQuotas
            .Include(q => q.Provider)
            .FirstOrDefaultAsync(q => q.Provider.ProviderId == providerId, ct);
        if (quota is null) return NotFound();

        quota.DailyTokensUsed = 0;
        quota.IsSuspended = false;
        quota.DailyResetAt = DateTimeOffset.UtcNow;
        quota.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Mapping helpers ───────────────────────────────────────────

    private static LlmProviderDto MapToDto(LlmProviderEntity p) =>
        new(p.Id, p.ProviderId, p.Name, p.Protocol, p.BaseUrl, p.ApiKey is not null,
            p.Description, p.IsEnabled, p.CreatedAt, p.UpdatedAt);

    private static LlmProviderDetailDto MapToDetailDto(LlmProviderEntity p) =>
        new(p.Id, p.ProviderId, p.Name, p.Protocol, p.BaseUrl, p.ApiKey is not null,
            p.Description, p.IsEnabled,
            p.Quota is null ? null : MapQuotaToDto(p.Quota),
            p.Models.OrderBy(m => m.SortOrder).Select(MapModelToDto).ToList(),
            p.CreatedAt, p.UpdatedAt);

    private static LlmProviderQuotaDto MapQuotaToDto(LlmProviderQuotaEntity q) =>
        new(q.DailyTokenLimit, q.MonthlyTokenLimit, q.DailyTokensUsed, q.MonthlyTokensUsed,
            q.IsSuspended, q.DailyResetAt, q.MonthlyResetAt, q.UpdatedAt);

    private static LlmModelDto MapModelToDto(LlmModelEntity m)
    {
        var tags = TryParseStringList(m.CapabilityTagsJson);
        return new(m.Id, m.ProviderId, m.ModelId, m.Name, m.Description,
            m.MaxContextTokens, m.MaxOutputTokens, m.InputPricePer1MTokens, m.OutputPricePer1MTokens,
            tags, m.IsDeprecated, m.IsDefault, m.SortOrder, m.CreatedAt, m.UpdatedAt);
    }

    private static List<string> TryParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
