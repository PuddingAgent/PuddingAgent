using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Skill 包管理 API——上传 zip/tar.gz、重命名、更新版本、删除。</summary>
[Authorize]
[ApiController]
[Route("api/skill-packages")]
public class SkillPackageApiController(
    PlatformDbContext db,
    MinioStorageService minio,
    ILogger<SkillPackageApiController> logger) : ControllerBase
{
    private static readonly string[] AllowedExtensions = [".zip", ".tar.gz"];

    // GET /api/skill-packages
    [HttpGet]
    public async Task<ActionResult<List<SkillPackageDto>>> List(CancellationToken ct)
    {
        var list = await db.SkillPackages
            .AsNoTracking()
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => ToDto(s))
            .ToListAsync(ct);
        return Ok(list);
    }

    // GET /api/skill-packages/{skillPackageId}
    [HttpGet("{skillPackageId}")]
    public async Task<ActionResult<SkillPackageDto>> Get(string skillPackageId, CancellationToken ct)
    {
        var entity = await FindAsync(skillPackageId, ct);
        return entity is null ? NotFound() : Ok(ToDto(entity));
    }

    // POST /api/skill-packages  (multipart/form-data)
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
        // 参数校验
        if (!System.Text.RegularExpressions.Regex.IsMatch(skillPackageId, @"^[a-z0-9\-]+$"))
            return BadRequest(new { error = "skillPackageId 只允许小写字母、数字和连字符" });

        if (!IsAllowedFile(file.FileName))
            return BadRequest(new { error = "仅支持 .zip 或 .tar.gz 格式" });

        if (await db.SkillPackages.AnyAsync(s => s.SkillPackageId == skillPackageId, ct))
            return Conflict(new { error = $"SkillPackageId '{skillPackageId}' 已存在" });

        var ver = version ?? "1.0.0";
        var objectKey = MinioStorageService.BuildObjectKey(skillPackageId, ver, SanitizeFileName(file.FileName));

        await using var stream = file.OpenReadStream();
        await minio.UploadAsync(objectKey, stream, file.Length, file.ContentType ?? "application/zip", ct);

        var entity = new SkillPackageEntity
        {
            SkillPackageId = skillPackageId,
            Name           = name,
            Description    = description,
            Version        = ver,
            FileName       = file.FileName,
            ObjectKey      = objectKey,
            FileSizeBytes  = file.Length,
            ContentType    = file.ContentType ?? "application/zip",
            IsEnabled      = true,
            SortOrder      = sortOrder ?? 100,
        };
        db.SkillPackages.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[SkillPackage] Created id={Id} key={Key}", skillPackageId, objectKey);
        return CreatedAtAction(nameof(Get), new { skillPackageId }, ToDto(entity));
    }

    // PUT /api/skill-packages/{skillPackageId}  更新元数据（不更换文件）
    [HttpPut("{skillPackageId}")]
    public async Task<ActionResult<SkillPackageDto>> UpdateMeta(
        string skillPackageId,
        [FromBody] UpdateSkillPackageRequest req,
        CancellationToken ct)
    {
        var entity = await FindAsync(skillPackageId, ct);
        if (entity is null) return NotFound();

        entity.Name        = req.Name;
        entity.Description = req.Description;
        entity.IsEnabled   = req.IsEnabled;
        entity.SortOrder   = req.SortOrder;
        entity.UpdatedAt   = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    // PUT /api/skill-packages/{skillPackageId}/file  更新版本（重新上传文件）
    [HttpPut("{skillPackageId}/file")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<ActionResult<SkillPackageDto>> UpdateFile(
        string skillPackageId,
        [FromForm] string version,
        IFormFile file,
        CancellationToken ct)
    {
        var entity = await FindAsync(skillPackageId, ct);
        if (entity is null) return NotFound();

        if (!IsAllowedFile(file.FileName))
            return BadRequest(new { error = "仅支持 .zip 或 .tar.gz 格式" });

        // 删除旧对象
        try { await minio.DeleteAsync(entity.ObjectKey, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "[SkillPackage] Failed to delete old object {Key}", entity.ObjectKey); }

        var newKey = MinioStorageService.BuildObjectKey(skillPackageId, version, SanitizeFileName(file.FileName));
        await using var stream = file.OpenReadStream();
        await minio.UploadAsync(newKey, stream, file.Length, file.ContentType ?? "application/zip", ct);

        entity.Version       = version;
        entity.FileName      = file.FileName;
        entity.ObjectKey     = newKey;
        entity.FileSizeBytes = file.Length;
        entity.ContentType   = file.ContentType ?? "application/zip";
        entity.UpdatedAt     = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("[SkillPackage] Updated file id={Id} ver={Ver} key={Key}", skillPackageId, version, newKey);
        return Ok(ToDto(entity));
    }

    // DELETE /api/skill-packages/{skillPackageId}
    [HttpDelete("{skillPackageId}")]
    public async Task<IActionResult> Delete(string skillPackageId, CancellationToken ct)
    {
        var entity = await FindAsync(skillPackageId, ct);
        if (entity is null) return NotFound();

        try { await minio.DeleteAsync(entity.ObjectKey, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "[SkillPackage] Failed to delete MinIO object {Key}", entity.ObjectKey); }

        db.SkillPackages.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // GET /api/skill-packages/{skillPackageId}/download-url  获取预签名下载链接
    [HttpGet("{skillPackageId}/download-url")]
    public async Task<ActionResult<object>> GetDownloadUrl(string skillPackageId, CancellationToken ct)
    {
        var entity = await FindAsync(skillPackageId, ct);
        if (entity is null) return NotFound();

        var url = await minio.GetPresignedDownloadUrlAsync(entity.ObjectKey, 86400, ct);
        return Ok(new { url, expiresInSeconds = 86400 });
    }

    // ── 辅助 ────────────────────────────────────────────────────────

    private Task<SkillPackageEntity?> FindAsync(string skillPackageId, CancellationToken ct) =>
        db.SkillPackages.FirstOrDefaultAsync(s => s.SkillPackageId == skillPackageId, ct);

    private static SkillPackageDto ToDto(SkillPackageEntity s) => new(
        s.Id, s.SkillPackageId, s.Name, s.Description, s.Version,
        s.FileName, s.FileSizeBytes, s.IsEnabled, s.SortOrder,
        s.CreatedAt, s.UpdatedAt);

    private static bool IsAllowedFile(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower.EndsWith(".zip") || lower.EndsWith(".tar.gz");
    }

    private static string SanitizeFileName(string fileName) =>
        System.IO.Path.GetFileName(fileName)
              .Replace(" ", "_")
              .Replace("..", ""); // 防止路径穿越
}
