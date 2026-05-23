using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// LLM 资源池 — 服务商管理 API（文件式配置，唯一来源 data/config/llm.providers.json）。
/// </summary>
[ApiController]
[Route("api/llm/providers")]
public class LlmProviderApiController(LlmProviderFileService fileService) : ControllerBase
{
    // ── 查询所有 provider ──────────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<LlmProviderDto>>> List(CancellationToken ct)
    {
        var providers = await fileService.ListProvidersAsync(ct);
        return Ok(providers);
    }

    // ── 查询单个 provider（含模型列表）─────────────────────
    [HttpGet("{providerId}")]
    public async Task<ActionResult<LlmProviderDetailDto>> Get(string providerId, CancellationToken ct)
    {
        var provider = await fileService.GetProviderAsync(providerId, ct);
        if (provider is null) return NotFound();
        return Ok(provider);
    }

    // ── 创建 provider ──────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<LlmProviderDto>> Create(
        [FromBody] UpsertLlmProviderRequest req, CancellationToken ct)
    {
        try
        {
            var result = await fileService.CreateProviderAsync(req, ct);
            return CreatedAtAction(nameof(Get), new { providerId = result.ProviderId }, result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    // ── 更新 provider ──────────────────────────────────────
    [HttpPut("{providerId}")]
    public async Task<ActionResult<LlmProviderDto>> Update(
        string providerId, [FromBody] UpsertLlmProviderRequest req, CancellationToken ct)
    {
        try
        {
            var result = await fileService.UpdateProviderAsync(providerId, req, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // ── 删除 provider ──────────────────────────────────────
    [HttpDelete("{providerId}")]
    public async Task<IActionResult> Delete(string providerId, CancellationToken ct)
    {
        try
        {
            await fileService.DeleteProviderAsync(providerId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // ── 配额管理（保留接口，返回空配额 — 配额上限已移至文件，用量在 TokenUsageStats）──

    [HttpGet("{providerId}/quota")]
    public ActionResult<LlmProviderQuotaDto> GetQuota(string providerId)
    {
        // 配额上限在 llm.providers.json 中，实际用量在 TokenUsageStats
        return Ok(new LlmProviderQuotaDto(null, null, 0, 0, false, null, null, DateTimeOffset.UtcNow));
    }

    [HttpPut("{providerId}/quota")]
    public ActionResult<LlmProviderQuotaDto> UpsertQuota(string providerId, [FromBody] UpdateQuotaRequest req)
    {
        // 配额上限配置已移入 llm.providers.json，未来通过文件 API 修改
        return Ok(new LlmProviderQuotaDto(req.DailyTokenLimit, req.MonthlyTokenLimit, 0, 0, false, null, null, DateTimeOffset.UtcNow));
    }

    [HttpPost("{providerId}/quota/reset-daily")]
    public IActionResult ResetDailyQuota()
    {
        return NoContent();
    }
}
