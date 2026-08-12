using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// LLM 资源池 — 模型管理 API。
/// 读写 data/config/llm.providers.json（通过 LlmProviderFileService）。
/// </summary>
[ApiController]
[Route("api/llm/providers/{providerId}/models")]
public class LlmModelApiController(
    LlmProviderFileService service,
    ILogger<LlmModelApiController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<LlmModelDto>>> List(string providerId, CancellationToken ct)
        => Ok(await service.ListModelsAsync(providerId, ct));

    [HttpGet("{modelId}")]
    public async Task<ActionResult<LlmModelDto>> Get(string providerId, string modelId, CancellationToken ct)
    {
        var models = await service.ListModelsAsync(providerId, ct);
        var result = models.FirstOrDefault(m => m.ModelId == modelId);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<LlmModelDto>> Create(
        string providerId, [FromBody] UpsertLlmModelRequest req, CancellationToken ct)
    {
        try
        {
            var result = await service.CreateModelAsync(providerId, req, ct);
            return CreatedAtAction(nameof(Get), new { providerId, modelId = result.ModelId }, result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[LlmModelApi] Create rejected provider={ProviderId}", providerId);
            return Conflict(new { error = "模型创建冲突，请检查模型 ID 是否已存在。" });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPut("{modelId}")]
    public async Task<ActionResult<LlmModelDto>> Update(
        string providerId, string modelId, [FromBody] UpsertLlmModelRequest req, CancellationToken ct)
    {
        try { return Ok(await service.UpdateModelAsync(providerId, modelId, req, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpDelete("{modelId}")]
    public async Task<IActionResult> Delete(string providerId, string modelId, CancellationToken ct)
    {
        try
        {
            await service.DeleteModelAsync(providerId, modelId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
