using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// LLM 资源池 — 服务商管理 API。
/// 读写 data/config/llm.providers.json（通过 LlmProviderFileService）。
/// </summary>
[ApiController]
[Route("api/llm/providers")]
public class LlmProviderApiController(LlmProviderFileService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<LlmProviderDto>>> List(CancellationToken ct)
        => Ok(await service.ListProvidersAsync(ct));

    [HttpGet("{providerId}")]
    public async Task<ActionResult<LlmProviderDetailDto>> Get(string providerId, CancellationToken ct)
    {
        var result = await service.GetProviderAsync(providerId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<LlmProviderDto>> Create(
        [FromBody] UpsertLlmProviderRequest req, CancellationToken ct)
    {
        try
        {
            var result = await service.CreateProviderAsync(req, ct);
            return CreatedAtAction(nameof(Get), new { providerId = result.ProviderId }, result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("{providerId}")]
    public async Task<ActionResult<LlmProviderDto>> Update(
        string providerId, [FromBody] UpsertLlmProviderRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await service.UpdateProviderAsync(providerId, req, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpDelete("{providerId}")]
    public async Task<IActionResult> Delete(string providerId, CancellationToken ct)
    {
        try
        {
            await service.DeleteProviderAsync(providerId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("{providerId}/quota")]
    public IActionResult GetQuota(string providerId)
        => NoContent();

    [HttpPut("{providerId}/quota")]
    public IActionResult UpsertQuota(string providerId)
        => NoContent();

    [HttpPost("{providerId}/quota/reset-daily")]
    public IActionResult ResetDailyQuota(string providerId)
        => NoContent();
}
