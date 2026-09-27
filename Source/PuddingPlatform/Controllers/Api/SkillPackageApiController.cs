using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Skills;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Skill 包管理 HTTP 出口——上传 zip/tar.gz、重命名、更新版本、删除。
/// 校验、对象键构造与旧对象清理都在 SkillPackageService（Web 与原生客户端共用），控制器只做映射。
/// </summary>
[Authorize]
[ApiController]
[Route("api/skill-packages")]
public class SkillPackageApiController(
    SkillPackageService packages,
    ILogger<SkillPackageApiController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<SkillPackageDto>>> List(CancellationToken ct)
        => Ok(await packages.ListAsync(ct));

    [HttpGet("{skillPackageId}")]
    public async Task<ActionResult<SkillPackageDto>> Get(string skillPackageId, CancellationToken ct)
        => await packages.GetAsync(skillPackageId, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpPost]
    [RequestSizeLimit(200 * 1024 * 1024)]  // 200 MB
    public async Task<ActionResult<SkillPackageDto>> Upload(
        [FromForm] string skillPackageId,
        [FromForm] string name,
        [FromForm] string? description,
        [FromForm] string? version,
        [FromForm] int? sortOrder,
        IFormFile file,
        CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var result = await packages.UploadAsync(
            new SkillPackageUpload(skillPackageId, name, description, version, sortOrder),
            new SkillPackageFile(file.FileName, file.Length, file.ContentType ?? "application/zip", stream), ct);
        return result.Status == SkillHubStatus.Ok
            ? CreatedAtAction(nameof(Get), new { skillPackageId }, result.Value)
            : Problem(result, skillPackageId);
    }

    [HttpPut("{skillPackageId}")]
    public async Task<ActionResult<SkillPackageDto>> UpdateMeta(
        string skillPackageId, [FromBody] UpdateSkillPackageRequest req, CancellationToken ct)
    {
        var result = await packages.UpdateMetaAsync(skillPackageId, req, ct);
        return result.Status == SkillHubStatus.Ok ? Ok(result.Value) : Problem(result, skillPackageId);
    }

    [HttpPut("{skillPackageId}/file")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<ActionResult<SkillPackageDto>> UpdateFile(
        string skillPackageId, [FromForm] string version, IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var result = await packages.UpdateFileAsync(skillPackageId, version,
            new SkillPackageFile(file.FileName, file.Length, file.ContentType ?? "application/zip", stream), ct);
        return result.Status == SkillHubStatus.Ok ? Ok(result.Value) : Problem(result, skillPackageId);
    }

    [HttpDelete("{skillPackageId}")]
    public async Task<IActionResult> Delete(string skillPackageId, CancellationToken ct)
    {
        var result = await packages.DeleteAsync(skillPackageId, ct);
        return result.Status == SkillHubStatus.Ok ? NoContent() : Problem(result, skillPackageId);
    }

    [HttpGet("{skillPackageId}/download-url")]
    public async Task<ActionResult<object>> GetDownloadUrl(string skillPackageId, CancellationToken ct)
    {
        var result = await packages.GetDownloadUrlAsync(skillPackageId, 86400, ct);
        return result.Status == SkillHubStatus.Ok
            ? Ok(new { url = result.Value, expiresInSeconds = 86400 })
            : Problem(result, skillPackageId);
    }

    /// <summary>Maps the shared semantic result onto the HTTP status codes the Web client already expects.</summary>
    private ActionResult Problem<T>(SkillHubResult<T> result, string skillPackageId) where T : class
    {
        if (result.Status == SkillHubStatus.BadRequest) return BadRequest(new { error = result.Error });
        if (result.Status == SkillHubStatus.Conflict) return Conflict(new { error = result.Error });
        logger.LogWarning("[SkillPackage] {Status} id={Id} error={Error}", result.Status, skillPackageId, result.Error);
        return NotFound();
    }
}