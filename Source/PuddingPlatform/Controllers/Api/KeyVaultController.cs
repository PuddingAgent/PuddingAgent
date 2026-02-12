using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// KeyVault API：
/// - 机密管理（增删改查）；
/// - 文本注入（{{vault:name}} -> 明文）；
/// - 文本脱敏（明文 -> [REDACTED:name]）。
/// </summary>
[Authorize]
[ApiController]
[Route("api/keyvault")]
public class KeyVaultController(
    IKeyVaultService keyVaultService,
    ILogger<KeyVaultController> logger) : ControllerBase
{
    [HttpPost("secrets")]
    public async Task<ActionResult<KeyVaultSecretDto>> Create(
        [FromBody] CreateKeyVaultSecretRequest request,
        CancellationToken ct)
    {
        try
        {
            var created = await keyVaultService.CreateSecretAsync(new CreateKeyVaultSecretCommand
            {
                Name = request.Name,
                Value = request.Value,
                Description = request.Description,
                Category = request.Category,
                Tags = request.Tags,
            }, ct);

            return CreatedAtAction(nameof(Get), new { id = created.KeyVaultId, confirm = false }, ToDto(created));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[KeyVaultApi] 创建密钥失败，name={Name}", request.Name);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("secrets")]
    public async Task<ActionResult<List<KeyVaultSecretDto>>> List(CancellationToken ct)
    {
        var list = await keyVaultService.ListSecretsAsync(ct);
        return Ok(list.Select(ToDto).ToList());
    }

    /// <summary>
    /// 获取密钥详情。
    /// 说明：返回明文必须显式传入 confirm=true。
    /// </summary>
    [HttpGet("secrets/{id}")]
    public async Task<ActionResult<KeyVaultSecretDetailDto>> Get(
        string id,
        [FromQuery] bool confirm,
        CancellationToken ct)
    {
        if (!confirm)
            return BadRequest(new { error = "获取明文必须显式确认：请传入 ?confirm=true" });

        try
        {
            var secret = await keyVaultService.GetSecretAsync(id, includePlainText: true, ct);
            if (secret is null) return NotFound();

            return Ok(ToDetailDto(secret));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[KeyVaultApi] 读取明文失败，keyVaultId={KeyVaultId}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("secrets/{id}")]
    public async Task<ActionResult<KeyVaultSecretDto>> Update(
        string id,
        [FromBody] UpdateKeyVaultSecretRequest request,
        CancellationToken ct)
    {
        try
        {
            var updated = await keyVaultService.UpdateSecretAsync(id, new UpdateKeyVaultSecretCommand
            {
                Name = request.Name,
                Value = request.Value,
                Description = request.Description,
                Category = request.Category,
                Tags = request.Tags,
            }, ct);

            if (updated is null) return NotFound();
            return Ok(ToDto(updated));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[KeyVaultApi] 更新密钥失败，keyVaultId={KeyVaultId}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("secrets/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var deleted = await keyVaultService.DeleteSecretAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("inject")]
    public async Task<ActionResult<KeyVaultTextTransformResponse>> Inject(
        [FromBody] KeyVaultTextTransformRequest request,
        CancellationToken ct)
    {
        var transformed = await keyVaultService.InjectAsync(request.Text ?? string.Empty, ct);
        return Ok(new KeyVaultTextTransformResponse(transformed));
    }

    [HttpPost("strip")]
    public async Task<ActionResult<KeyVaultTextTransformResponse>> Strip(
        [FromBody] KeyVaultTextTransformRequest request,
        CancellationToken ct)
    {
        var transformed = await keyVaultService.StripAsync(request.Text ?? string.Empty, ct);
        return Ok(new KeyVaultTextTransformResponse(transformed));
    }

    private static KeyVaultSecretDto ToDto(KeyVaultSecretSummary x) => new(
        x.Id,
        x.KeyVaultId,
        x.Name,
        x.Description,
        x.Category,
        x.Tags.ToList(),
        x.CreatedAt,
        x.UpdatedAt);

    private static KeyVaultSecretDetailDto ToDetailDto(KeyVaultSecretDetail x) => new(
        x.Id,
        x.KeyVaultId,
        x.Name,
        x.Description,
        x.Category,
        x.Tags.ToList(),
        x.Value,
        x.CreatedAt,
        x.UpdatedAt);
}
