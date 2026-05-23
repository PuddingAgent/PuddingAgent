using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// LLM 资源池 — 服务商管理 API（DB 主源）。启动时从 default-data/config/llm.providers.json 种子导入。
/// </summary>
[ApiController]
[Route("api/llm/providers")]
public class LlmProviderApiController(PlatformDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<LlmProviderDto>>> List(CancellationToken ct)
    {
        var providers = await db.LlmProviders.AsNoTracking()
            .OrderBy(p => p.ProviderId).ToListAsync(ct);
        return Ok(providers.Select(p => MapToDto(p)).ToList());
    }

    [HttpGet("{providerId}")]
    public async Task<ActionResult<LlmProviderDetailDto>> Get(string providerId, CancellationToken ct)
    {
        var provider = await db.LlmProviders.AsNoTracking()
            .Include(p => p.Models.OrderBy(m => m.SortOrder))
            .Include(p => p.Quota)
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (provider is null) return NotFound();
        return Ok(MapToDetailDto(provider));
    }

    [HttpPost]
    public async Task<ActionResult<LlmProviderDto>> Create(
        [FromBody] UpsertLlmProviderRequest req, CancellationToken ct)
    {
        if (await db.LlmProviders.AnyAsync(p => p.ProviderId == req.ProviderId, ct))
            return Conflict(new { error = $"Provider '{req.ProviderId}' already exists" });

        var entity = MapToEntity(req);
        entity.Quota = new LlmProviderQuotaEntity { ProviderId = entity.Id };
        db.LlmProviders.Add(entity);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { providerId = entity.ProviderId }, MapToDto(entity));
    }

    [HttpPut("{providerId}")]
    public async Task<ActionResult<LlmProviderDto>> Update(
        string providerId, [FromBody] UpsertLlmProviderRequest req, CancellationToken ct)
    {
        var entity = await db.LlmProviders.FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (entity is null) return NotFound();

        ApplyUpdate(entity, req);
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(MapToDto(entity));
    }

    [HttpDelete("{providerId}")]
    public async Task<IActionResult> Delete(string providerId, CancellationToken ct)
    {
        var entity = await db.LlmProviders
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (entity is null) return NotFound();

        db.LlmProviders.Remove(entity); // Cascade deletes models + quota
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Quota stubs (quota limits in DB, usage in TokenUsageStats) ──

    [HttpGet("{providerId}/quota")]
    public async Task<ActionResult<LlmProviderQuotaDto>> GetQuota(string providerId, CancellationToken ct)
    {
        var p = await db.LlmProviders.AsNoTracking()
            .Include(p => p.Quota)
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (p?.Quota is null) return Ok(EmptyQuota());
        return Ok(MapToQuotaDto(p.Quota));
    }

    [HttpPut("{providerId}/quota")]
    public async Task<ActionResult<LlmProviderQuotaDto>> UpsertQuota(
        string providerId, [FromBody] UpdateQuotaRequest req, CancellationToken ct)
    {
        var provider = await db.LlmProviders.Include(p => p.Quota)
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (provider is null) return NotFound();

        var quota = provider.Quota ?? new LlmProviderQuotaEntity();
        quota.DailyTokenLimit = req.DailyTokenLimit;
        quota.MonthlyTokenLimit = req.MonthlyTokenLimit;
        quota.UpdatedAt = DateTimeOffset.UtcNow;
        if (provider.Quota is null) { provider.Quota = quota; db.LlmProviderQuotas.Add(quota); }
        await db.SaveChangesAsync(ct);
        return Ok(MapToQuotaDto(quota));
    }

    [HttpPost("{providerId}/quota/reset-daily")]
    public async Task<IActionResult> ResetDailyQuota(string providerId, CancellationToken ct)
    {
        var q = await db.LlmProviderQuotas
            .FirstOrDefaultAsync(q => q.Provider != null && q.Provider.ProviderId == providerId, ct);
        if (q is not null) { q.DailyTokensUsed = 0; await db.SaveChangesAsync(ct); }
        return NoContent();
    }

    // ── Mapping helpers ──────────────────────────────────────────

    private static LlmProviderDto MapToDto(LlmProviderEntity e) => new(
        e.Id, e.ProviderId, e.Name, e.Protocol, e.BaseUrl,
        HasApiKey: !string.IsNullOrEmpty(e.ApiKey),
        e.Description, e.IsEnabled, e.CreatedAt, e.UpdatedAt
    );

    private static LlmProviderDetailDto MapToDetailDto(LlmProviderEntity e) => new(
        e.Id, e.ProviderId, e.Name, e.Protocol, e.BaseUrl,
        HasApiKey: !string.IsNullOrEmpty(e.ApiKey),
        e.Description, e.IsEnabled,
        Quota: e.Quota is not null ? MapToQuotaDto(e.Quota) : EmptyQuota(),
        Models: (e.Models ?? []).Select(MapToModelDto).ToList(),
        e.CreatedAt, e.UpdatedAt
    );

    private static LlmModelDto MapToModelDto(LlmModelEntity m) => new(
        m.Id, m.ProviderId, m.ModelId, m.Name, m.Description,
        m.MaxContextTokens, m.MaxOutputTokens,
        m.InputPricePer1MTokens, m.OutputPricePer1MTokens, m.CacheHitPricePer1MTokens,
        CapabilityTags: string.IsNullOrEmpty(m.CapabilityTagsJson)
            ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(m.CapabilityTagsJson) ?? [],
        m.IsDeprecated, m.IsDefault, m.SortOrder, m.CreatedAt, m.UpdatedAt
    );

    private static LlmProviderQuotaDto MapToQuotaDto(LlmProviderQuotaEntity q) => new(
        q.DailyTokenLimit, q.MonthlyTokenLimit, q.DailyTokensUsed, q.MonthlyTokensUsed,
        q.IsSuspended, q.DailyResetAt, q.MonthlyResetAt, q.UpdatedAt
    );

    private static LlmProviderQuotaDto EmptyQuota() =>
        new(null, null, 0, 0, false, null, null, DateTimeOffset.UtcNow);

    private static LlmProviderEntity MapToEntity(UpsertLlmProviderRequest req) => new()
    {
        ProviderId = req.ProviderId, Name = req.Name, Protocol = req.Protocol,
        BaseUrl = req.BaseUrl, ApiKey = req.ApiKey, Description = req.Description,
        IsEnabled = req.IsEnabled, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static void ApplyUpdate(LlmProviderEntity e, UpsertLlmProviderRequest req)
    {
        e.Name = req.Name; e.Protocol = req.Protocol; e.BaseUrl = req.BaseUrl;
        if (req.ApiKey is not null) e.ApiKey = req.ApiKey;
        e.Description = req.Description; e.IsEnabled = req.IsEnabled;
    }
}
