using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Object storage the skill-package service writes to. Narrow on purpose: the application service must be
/// testable without a live MinIO server, and object storage is an infrastructure detail, not a business rule.
/// </summary>
public interface ISkillPackageObjectStore
{
    Task<string> UploadAsync(string objectKey, Stream stream, long size, string contentType, CancellationToken ct = default);
    Task<string> GetPresignedDownloadUrlAsync(string objectKey, int expirySeconds = 86400, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}

/// <summary>One uploaded package file. The caller owns the stream.</summary>
public sealed record SkillPackageFile(string FileName, long SizeBytes, string ContentType, Stream Content);

public sealed record SkillPackageUpload(string SkillPackageId, string Name, string? Description, string? Version, int? SortOrder);

/// <summary>
/// Skill 包（旧文件包）应用操作——从 SkillPackageApiController 原位下沉，Web 与原生客户端共用同一份
/// 校验、对象键构造与旧对象清理逻辑。控制器只做 HTTP 映射，不再持有业务规则。
/// </summary>
public sealed class SkillPackageService(
    PlatformDbContext db,
    ISkillPackageObjectStore store,
    ILogger<SkillPackageService> logger)
{
    public static readonly IReadOnlyList<string> AllowedExtensions = [".zip", ".tar.gz"];

    private static readonly Regex s_idPattern = new("^[a-z0-9\\-]+$", RegexOptions.Compiled);

    public async Task<List<SkillPackageDto>> ListAsync(CancellationToken ct = default)
        => await db.SkillPackages.AsNoTracking()
            .OrderBy(package => package.SortOrder).ThenBy(package => package.Id)
            .Select(package => ToDto(package))
            .ToListAsync(ct);

    public async Task<SkillPackageDto?> GetAsync(string skillPackageId, CancellationToken ct = default)
        => await FindAsync(skillPackageId, ct) is { } entity ? ToDto(entity) : null;

    /// <summary>Publishes a brand-new package. A duplicate id is a conflict, never an overwrite.</summary>
    public async Task<SkillHubResult<SkillPackageDto>> UploadAsync(
        SkillPackageUpload upload, SkillPackageFile file, CancellationToken ct = default)
    {
        if (!IsValidId(upload.SkillPackageId))
            return SkillHubResult<SkillPackageDto>.BadRequest("skillPackageId 只允许小写字母、数字和连字符");
        if (string.IsNullOrWhiteSpace(upload.Name))
            return SkillHubResult<SkillPackageDto>.BadRequest("技能包名称不能为空。");
        if (!IsAllowedFile(file.FileName))
            return SkillHubResult<SkillPackageDto>.BadRequest($"仅支持 {string.Join(" 或 ", AllowedExtensions)} 格式");
        if (await db.SkillPackages.AnyAsync(package => package.SkillPackageId == upload.SkillPackageId, ct))
            return SkillHubResult<SkillPackageDto>.Conflict($"SkillPackageId '{upload.SkillPackageId}' 已存在");

        var version = string.IsNullOrWhiteSpace(upload.Version) ? "1.0.0" : upload.Version.Trim();
        var objectKey = MinioStorageService.BuildObjectKey(upload.SkillPackageId, version, SanitizeFileName(file.FileName));
        await store.UploadAsync(objectKey, file.Content, file.SizeBytes, file.ContentType, ct);

        var entity = new SkillPackageEntity
        {
            SkillPackageId = upload.SkillPackageId,
            Name = upload.Name,
            Description = upload.Description,
            Version = version,
            FileName = file.FileName,
            ObjectKey = objectKey,
            FileSizeBytes = file.SizeBytes,
            ContentType = file.ContentType,
            IsEnabled = true,
            SortOrder = upload.SortOrder ?? 100,
        };
        db.SkillPackages.Add(entity);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("[SkillPackage] Created id={Id} key={Key}", upload.SkillPackageId, objectKey);
        return SkillHubResult<SkillPackageDto>.Ok(ToDto(entity));
    }

    /// <summary>Metadata only: the stored file, version and object key are untouched.</summary>
    public async Task<SkillHubResult<SkillPackageDto>> UpdateMetaAsync(
        string skillPackageId, UpdateSkillPackageRequest request, CancellationToken ct = default)
    {
        var entity = await FindAsync(skillPackageId, ct);
        if (entity is null) return SkillHubResult<SkillPackageDto>.NotFound($"SkillPackageId '{skillPackageId}' 不存在");
        if (string.IsNullOrWhiteSpace(request.Name))
            return SkillHubResult<SkillPackageDto>.BadRequest("技能包名称不能为空。");

        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.IsEnabled = request.IsEnabled;
        entity.SortOrder = request.SortOrder;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return SkillHubResult<SkillPackageDto>.Ok(ToDto(entity));
    }

    /// <summary>
    /// Replaces the stored file with a new version. Validation happens before any object is touched, so a
    /// rejected upload cannot leave the package without a file.
    /// </summary>
    public async Task<SkillHubResult<SkillPackageDto>> UpdateFileAsync(
        string skillPackageId, string version, SkillPackageFile file, CancellationToken ct = default)
    {
        var entity = await FindAsync(skillPackageId, ct);
        if (entity is null) return SkillHubResult<SkillPackageDto>.NotFound($"SkillPackageId '{skillPackageId}' 不存在");
        if (string.IsNullOrWhiteSpace(version))
            return SkillHubResult<SkillPackageDto>.BadRequest("版本号不能为空。");
        if (!IsAllowedFile(file.FileName))
            return SkillHubResult<SkillPackageDto>.BadRequest($"仅支持 {string.Join(" 或 ", AllowedExtensions)} 格式");

        var previousKey = entity.ObjectKey;
        var newKey = MinioStorageService.BuildObjectKey(skillPackageId, version.Trim(), SanitizeFileName(file.FileName));
        await store.UploadAsync(newKey, file.Content, file.SizeBytes, file.ContentType, ct);
        // The new object exists before the old one is removed; a failed delete must not lose the upload.
        if (!string.Equals(previousKey, newKey, StringComparison.Ordinal))
        {
            try { await store.DeleteAsync(previousKey, ct); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "[SkillPackage] Failed to delete old object {Key}", previousKey);
            }
        }

        entity.Version = version.Trim();
        entity.FileName = file.FileName;
        entity.ObjectKey = newKey;
        entity.FileSizeBytes = file.SizeBytes;
        entity.ContentType = file.ContentType;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("[SkillPackage] Updated file id={Id} ver={Ver} key={Key}", skillPackageId, version, newKey);
        return SkillHubResult<SkillPackageDto>.Ok(ToDto(entity));
    }

    /// <summary>Removes the row and its object; a failing object delete is logged, not silently ignored.</summary>
    public async Task<SkillHubResult<SkillPackageDto>> DeleteAsync(string skillPackageId, CancellationToken ct = default)
    {
        var entity = await FindAsync(skillPackageId, ct);
        if (entity is null) return SkillHubResult<SkillPackageDto>.NotFound($"SkillPackageId '{skillPackageId}' 不存在");

        try { await store.DeleteAsync(entity.ObjectKey, ct); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[SkillPackage] Failed to delete object {Key}", entity.ObjectKey);
        }

        db.SkillPackages.Remove(entity);
        await db.SaveChangesAsync(ct);
        return SkillHubResult<SkillPackageDto>.Ok(ToDto(entity));
    }

    /// <summary>Presigned download URL. The native client opens it; it does not proxy the bytes.</summary>
    public async Task<SkillHubResult<string>> GetDownloadUrlAsync(
        string skillPackageId, int expirySeconds = 86400, CancellationToken ct = default)
    {
        var entity = await FindAsync(skillPackageId, ct);
        if (entity is null) return SkillHubResult<string>.NotFound($"SkillPackageId '{skillPackageId}' 不存在");
        var url = await store.GetPresignedDownloadUrlAsync(entity.ObjectKey, expirySeconds, ct);
        return SkillHubResult<string>.Ok(url);
    }

    public static bool IsValidId(string? skillPackageId) =>
        !string.IsNullOrWhiteSpace(skillPackageId) && s_idPattern.IsMatch(skillPackageId);

    /// <summary>Core accepts .zip and .tar.gz only today; .tgz listed on the Web card is not accepted here.</summary>
    public static bool IsAllowedFile(string? fileName)
    {
        var lower = (fileName ?? "").ToLowerInvariant();
        return AllowedExtensions.Any(extension => lower.EndsWith(extension, StringComparison.Ordinal));
    }

    public static string SanitizeFileName(string fileName) =>
        Path.GetFileName(fileName).Replace(" ", "_").Replace("..", "");

    private Task<SkillPackageEntity?> FindAsync(string skillPackageId, CancellationToken ct) =>
        db.SkillPackages.FirstOrDefaultAsync(package => package.SkillPackageId == skillPackageId, ct);

    private static SkillPackageDto ToDto(SkillPackageEntity entity) => new(
        entity.Id, entity.SkillPackageId, entity.Name, entity.Description, entity.Version,
        entity.FileName, entity.FileSizeBytes, entity.IsEnabled, entity.SortOrder,
        entity.CreatedAt, entity.UpdatedAt);
}
