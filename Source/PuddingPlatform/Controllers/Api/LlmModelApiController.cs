using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>LLM 资源池 — 模型管理 API</summary>
[ApiController]
[Route("api/llm/providers/{providerId}/models")]
public class LlmModelApiController(PlatformDbContext db) : ControllerBase
{
    // ── 查询某 provider 下所有模型 ────────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<LlmModelDto>>> List(string providerId, CancellationToken ct)
    {
        var provider = await db.LlmProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (provider is null) return NotFound(new { error = $"Provider '{providerId}' 不存在" });

        var models = await db.LlmModels.AsNoTracking()
            .Where(m => m.ProviderId == provider.Id)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        return Ok(models.Select(MapToDto).ToList());
    }

    // ── 查询单个模型 ──────────────────────────────────────────────
    [HttpGet("{modelId}")]
    public async Task<ActionResult<LlmModelDto>> Get(
        string providerId, string modelId, CancellationToken ct)
    {
        var m = await FindModelAsync(providerId, modelId, ct);
        return m is null ? NotFound() : Ok(MapToDto(m));
    }

    // ── 创建模型 ──────────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<LlmModelDto>> Create(
        string providerId, [FromBody] UpsertLlmModelRequest req, CancellationToken ct)
    {
        var provider = await db.LlmProviders.FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (provider is null) return NotFound(new { error = $"Provider '{providerId}' 不存在" });

        if (await db.LlmModels.AnyAsync(m => m.ProviderId == provider.Id && m.ModelId == req.ModelId, ct))
            return Conflict(new { error = $"ModelId '{req.ModelId}' 在该 Provider 下已存在" });

        var entity = new LlmModelEntity
        {
            ProviderId = provider.Id,
            ModelId = req.ModelId,
            Name = req.Name,
            Description = req.Description,
            MaxContextTokens = req.MaxContextTokens,
            InputPricePer1MTokens = req.InputPricePer1MTokens,
            OutputPricePer1MTokens = req.OutputPricePer1MTokens,
            CapabilityTagsJson = JsonSerializer.Serialize(req.CapabilityTags ?? []),
            IsDeprecated = req.IsDeprecated,
            IsDefault = req.IsDefault,
            SortOrder = req.SortOrder,
        };
        db.LlmModels.Add(entity);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get),
            new { providerId, modelId = entity.ModelId }, MapToDto(entity));
    }

    // ── 更新模型 ──────────────────────────────────────────────────
    [HttpPut("{modelId}")]
    public async Task<ActionResult<LlmModelDto>> Update(
        string providerId, string modelId,
        [FromBody] UpsertLlmModelRequest req, CancellationToken ct)
    {
        var entity = await FindModelAsync(providerId, modelId, ct);
        if (entity is null) return NotFound();

        entity.Name = req.Name;
        entity.Description = req.Description;
        entity.MaxContextTokens = req.MaxContextTokens;
        entity.InputPricePer1MTokens = req.InputPricePer1MTokens;
        entity.OutputPricePer1MTokens = req.OutputPricePer1MTokens;
        entity.CapabilityTagsJson = JsonSerializer.Serialize(req.CapabilityTags ?? []);
        entity.IsDeprecated = req.IsDeprecated;
        entity.IsDefault = req.IsDefault;
        entity.SortOrder = req.SortOrder;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(MapToDto(entity));
    }

    // ── 删除模型 ──────────────────────────────────────────────────
    [HttpDelete("{modelId}")]
    public async Task<IActionResult> Delete(string providerId, string modelId, CancellationToken ct)
    {
        var entity = await FindModelAsync(providerId, modelId, ct);
        if (entity is null) return NotFound();

        db.LlmModels.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task<LlmModelEntity?> FindModelAsync(string providerId, string modelId, CancellationToken ct)
    {
        var p = await db.LlmProviders.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProviderId == providerId, ct);
        if (p is null) return null;

        return await db.LlmModels
            .FirstOrDefaultAsync(m => m.ProviderId == p.Id && m.ModelId == modelId, ct);
    }

    private static LlmModelDto MapToDto(LlmModelEntity m)
    {
        List<string> tags = [];
        if (!string.IsNullOrWhiteSpace(m.CapabilityTagsJson))
        {
            try { tags = JsonSerializer.Deserialize<List<string>>(m.CapabilityTagsJson) ?? []; }
            catch { /* ignore */ }
        }
        return new(m.Id, m.ProviderId, m.ModelId, m.Name, m.Description,
            m.MaxContextTokens, m.InputPricePer1MTokens, m.OutputPricePer1MTokens,
            tags, m.IsDeprecated, m.IsDefault, m.SortOrder, m.CreatedAt, m.UpdatedAt);
    }
}
