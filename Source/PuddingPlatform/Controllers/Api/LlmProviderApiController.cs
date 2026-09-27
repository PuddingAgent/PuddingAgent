using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// LLM 资源池 — 服务商管理 API。
/// 读写 data/config/llm.providers.json（通过 LlmProviderFileService）。
/// </summary>
[ApiController]
[Route("api/llm/providers")]
public class LlmProviderApiController(
    LlmProviderFileService service,
    LlmProviderQuotaService quotas,
    ILogger<LlmProviderApiController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<LlmProviderDto>>> List(CancellationToken ct)
        => Ok(await service.ListProvidersAsync(ct));

    [HttpGet("{providerId}")]
    public async Task<ActionResult<LlmProviderDetailDto>> Get(string providerId, CancellationToken ct)
    {
        var result = await service.GetProviderAsync(providerId, ct);
        if (result is null) return NotFound();
        // 配额 = 文件里的限额 + 账本推导的用量；provider 详情不再返回伪造的零值配额。
        var quota = await quotas.TryGetAsync(providerId, ct);
        return Ok(quota is null ? result : result with { Quota = quota });
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
            logger.LogWarning(ex, "[LlmProviderApi] Create rejected");
            return Conflict(new { error = "服务商创建冲突，请检查 providerId 是否已存在。" });
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
    public async Task<ActionResult<LlmProviderQuotaDto>> GetQuota(string providerId, CancellationToken ct)
        => await quotas.TryGetAsync(providerId, ct) is { } quota ? Ok(quota) : NotFound();

    [HttpPut("{providerId}/quota")]
    public async Task<ActionResult<LlmProviderQuotaDto>> UpsertQuota(
        string providerId, [FromBody] UpdateQuotaRequest request, CancellationToken ct)
    {
        try { return Ok(await quotas.UpsertAsync(providerId, request, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{providerId}/quota/reset-daily")]
    public async Task<ActionResult<LlmProviderQuotaDto>> ResetDailyQuota(string providerId, CancellationToken ct)
    {
        try { return Ok(await quotas.ResetDailyAsync(providerId, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── 余额查询（DeepSeek get-user-balance 等 OpenAI 兼容 provider）──

    /// <summary>
    /// 查询 provider 账户余额。GET 到 {baseUrl}/user/balance，Bearer 鉴权。
    /// apiKey 占位符、KeyVault、错误处理均在 LlmProviderFileService.GetBalanceAsync 实现。
    /// </summary>
    [HttpGet("{providerId}/balance")]
    public async Task<ActionResult<LlmProviderBalanceDto>> GetBalance(
        string providerId, CancellationToken ct)
    {
        try
        {
            return Ok(await service.GetBalanceAsync(providerId, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[LlmProviderApi] Balance rejected for {Provider}", providerId);
            return BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "[LlmProviderApi] Balance upstream error for {Provider}", providerId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "余额查询上游错误，请稍后重试或检查服务商配置。" });
        }
    }
}
