using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// LLM 资源池 — 模型管理 API（DB 主源）。模型通过 LlmProviderApiController 级联管理。
/// </summary>
[ApiController]
[Route("api/llm/providers/{providerId}/models")]
public class LlmModelApiController(PlatformDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<LlmModelDto>>> List(string providerId, CancellationToken ct)
    {
        var provider = await db.LlmProviders.AsNoTracking()
            .Include(p => p.Models.OrderBy(m => m.SortOrder))
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (provider is null) return NotFound();
        return Ok(provider.Models.Select(MapToDto).ToList());
    }

    [HttpGet("{modelId}")]
    public async Task<ActionResult<LlmModelDto>> Get(
        string providerId, string modelId, CancellationToken ct)
    {
        var model = await db.LlmModels.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Provider.ProviderId == providerId && m.ModelId == modelId, ct);
        if (model is null) return NotFound();
        return Ok(MapToDto(model));
    }

    [HttpPost]
    public async Task<ActionResult<LlmModelDto>> Create(
        string providerId, [FromBody] UpsertLlmModelRequest req, CancellationToken ct)
    {
        var provider = await db.LlmProviders
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);
        if (provider is null) return NotFound(new { error = $"Provider '{providerId}' not found" });

        if (provider.Models.Any(m => m.ModelId == req.ModelId))
            return Conflict(new { error = $"Model '{req.ModelId}' already exists in provider '{providerId}'" });

        var entity = MapToEntity(req, provider.Id);
        db.LlmModels.Add(entity);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { providerId, modelId = entity.ModelId }, MapToDto(entity));
    }

    [HttpPut("{modelId}")]
    public async Task<ActionResult<LlmModelDto>> Update(
        string providerId, string modelId,
        [FromBody] UpsertLlmModelRequest req, CancellationToken ct)
    {
        var model = await db.LlmModels
            .FirstOrDefaultAsync(m => m.Provider.ProviderId == providerId && m.ModelId == modelId, ct);
        if (model is null) return NotFound();

        ApplyUpdate(model, req);
        await db.SaveChangesAsync(ct);
        return Ok(MapToDto(model));
    }

    [HttpDelete("{modelId}")]
    public async Task<IActionResult> Delete(string providerId, string modelId, CancellationToken ct)
    {
        var model = await db.LlmModels
            .FirstOrDefaultAsync(m => m.Provider.ProviderId == providerId && m.ModelId == modelId, ct);
        if (model is null) return NotFound();

        db.LlmModels.Remove(model);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Mapping ──────────────────────────────────────────────────

    private static List<string> ParseTags(string? json) =>
        string.IsNullOrEmpty(json) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static string ToTagsJson(List<string>? tags) =>
        tags is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(tags) : "[]";

    private static LlmModelDto MapToDto(LlmModelEntity m) => new(
        m.Id, m.ProviderId, m.ModelId, m.Name, m.Description,
        m.MaxContextTokens, m.MaxOutputTokens,
        m.InputPricePer1MTokens, m.OutputPricePer1MTokens, m.CacheHitPricePer1MTokens,
        ParseTags(m.CapabilityTagsJson),
        m.IsDeprecated, m.IsDefault, m.SortOrder, m.CreatedAt, m.UpdatedAt
    );

    private static LlmModelEntity MapToEntity(UpsertLlmModelRequest req, int providerId) => new()
    {
        ProviderId = providerId, ModelId = req.ModelId, Name = req.Name,
        Description = req.Description,
        MaxContextTokens = req.MaxContextTokens, MaxOutputTokens = req.MaxOutputTokens,
        InputPricePer1MTokens = req.InputPricePer1MTokens,
        OutputPricePer1MTokens = req.OutputPricePer1MTokens,
        CacheHitPricePer1MTokens = req.CacheHitPricePer1MTokens,
        CapabilityTagsJson = ToTagsJson(req.CapabilityTags),
        IsDeprecated = req.IsDeprecated, IsDefault = req.IsDefault, SortOrder = req.SortOrder,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static void ApplyUpdate(LlmModelEntity m, UpsertLlmModelRequest req)
    {
        m.Name = req.Name; m.Description = req.Description;
        m.MaxContextTokens = req.MaxContextTokens; m.MaxOutputTokens = req.MaxOutputTokens;
        m.InputPricePer1MTokens = req.InputPricePer1MTokens;
        m.OutputPricePer1MTokens = req.OutputPricePer1MTokens;
        m.CacheHitPricePer1MTokens = req.CacheHitPricePer1MTokens;
        m.CapabilityTagsJson = ToTagsJson(req.CapabilityTags);
        m.IsDeprecated = req.IsDeprecated; m.IsDefault = req.IsDefault; m.SortOrder = req.SortOrder;
        m.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
