using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Skill 包实体——管理上传的 Skill 压缩包元数据。
/// </summary>
public class SkillPackageEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(128)]
    public string SkillPackageId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Description { get; set; }

    [MaxLength(64)]
    public string Version { get; set; } = "1.0.0";

    [MaxLength(256)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(512)]
    public string ObjectKey { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    [MaxLength(128)]
    public string ContentType { get; set; } = "application/zip";

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; } = 100;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
