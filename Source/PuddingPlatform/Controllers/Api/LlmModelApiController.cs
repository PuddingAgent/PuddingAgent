using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// LLM 资源池 — 模型管理 API（文件式配置，模型存储在 data/config/llm.providers.json 的 providers[].models[]）。
/// </summary>
[ApiController]
[Route("api/llm/providers/{providerId}/models")]
public class LlmModelApiController(LlmProviderFileService fileService) : ControllerBase
{
    // ── 查询某 provider 下所有模型 ────────────────────────────────
    [HttpGet]
    public async Task<ActionResult<List<LlmModelDto>>> List(string providerId, CancellationToken ct)
    {
        var models = await fileService.ListModelsAsync(providerId, ct);
        return Ok(models);
    }

    // ── 查询单个模型 ──────────────────────────────────────────────
    [HttpGet("{modelId}")]
    public async Task<ActionResult<LlmModelDto>> Get(
        string providerId, string modelId, CancellationToken ct)
    {
        var models = await fileService.ListModelsAsync(providerId, ct);
        var model = models.FirstOrDefault(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        return model is null ? NotFound() : Ok(model);
    }

    // ── 创建模型 ──────────────────────────────────────────────────
    [HttpPost]
    public async Task<ActionResult<LlmModelDto>> Create(
        string providerId, [FromBody] UpsertLlmModelRequest req, CancellationToken ct)
    {
        try
        {
            var result = await fileService.CreateModelAsync(providerId, req, ct);
            return CreatedAtAction(nameof(Get), new { providerId, modelId = result.ModelId }, result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    // ── 更新模型 ──────────────────────────────────────────────────
    [HttpPut("{modelId}")]
    public async Task<ActionResult<LlmModelDto>> Update(
        string providerId, string modelId,
        [FromBody] UpsertLlmModelRequest req, CancellationToken ct)
    {
        try
        {
            var result = await fileService.UpdateModelAsync(providerId, modelId, req, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // ── 删除模型 ──────────────────────────────────────────────────
    [HttpDelete("{modelId}")]
    public async Task<IActionResult> Delete(string providerId, string modelId, CancellationToken ct)
    {
        try
        {
            await fileService.DeleteModelAsync(providerId, modelId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
