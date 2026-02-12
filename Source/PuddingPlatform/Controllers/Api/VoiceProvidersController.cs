using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// TTS/ASR 语音服务商管理 API。
/// 读写 data/config/voice/providers.json（通过 VoiceProviderFileService）。
/// 与 LLM 资源池完全独立。
/// </summary>
[ApiController]
[Route("api/voice-providers")]
public class VoiceProvidersController(VoiceProviderFileService service) : ControllerBase
{
    // ── Provider CRUD ──────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<List<VoiceProviderDto>>> List(CancellationToken ct)
        => Ok(await service.ListProvidersAsync(ct));

    [HttpGet("{providerId}")]
    public async Task<ActionResult<VoiceProviderDetailDto>> Get(string providerId, CancellationToken ct)
    {
        var result = await service.GetProviderAsync(providerId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<VoiceProviderDto>> Create(
        [FromBody] UpsertVoiceProviderRequest req, CancellationToken ct)
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
    public async Task<ActionResult<VoiceProviderDto>> Update(
        string providerId, [FromBody] UpsertVoiceProviderRequest req, CancellationToken ct)
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

    // ── TTS Model CRUD ─────────────────────────────────────────

    [HttpGet("{providerId}/tts-models")]
    public async Task<IActionResult> ListTtsModels(string providerId, CancellationToken ct)
    {
        var result = await service.GetProviderAsync(providerId, ct);
        return result is null ? NotFound() : Ok(result.TtsModels);
    }

    [HttpPost("{providerId}/tts-models")]
    public async Task<ActionResult<TtsModelDto>> CreateTtsModel(
        string providerId, [FromBody] UpsertTtsModelRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await service.CreateTtsModelAsync(providerId, req, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("{providerId}/tts-models/{modelId}")]
    public async Task<ActionResult<TtsModelDto>> UpdateTtsModel(
        string providerId, string modelId, [FromBody] UpsertTtsModelRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await service.UpdateTtsModelAsync(providerId, modelId, req, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpDelete("{providerId}/tts-models/{modelId}")]
    public async Task<IActionResult> DeleteTtsModel(
        string providerId, string modelId, CancellationToken ct)
    {
        try
        {
            await service.DeleteTtsModelAsync(providerId, modelId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── ASR Model CRUD ─────────────────────────────────────────

    [HttpGet("{providerId}/asr-models")]
    public async Task<IActionResult> ListAsrModels(string providerId, CancellationToken ct)
    {
        var result = await service.GetProviderAsync(providerId, ct);
        return result is null ? NotFound() : Ok(result.AsrModels);
    }

    [HttpPost("{providerId}/asr-models")]
    public async Task<ActionResult<AsrModelDto>> CreateAsrModel(
        string providerId, [FromBody] UpsertAsrModelRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await service.CreateAsrModelAsync(providerId, req, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("{providerId}/asr-models/{modelId}")]
    public async Task<ActionResult<AsrModelDto>> UpdateAsrModel(
        string providerId, string modelId, [FromBody] UpsertAsrModelRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await service.UpdateAsrModelAsync(providerId, modelId, req, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpDelete("{providerId}/asr-models/{modelId}")]
    public async Task<IActionResult> DeleteAsrModel(
        string providerId, string modelId, CancellationToken ct)
    {
        try
        {
            await service.DeleteAsrModelAsync(providerId, modelId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
