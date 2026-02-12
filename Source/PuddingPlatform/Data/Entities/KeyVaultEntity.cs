using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// KeyVault 密钥保管箱实体——加密存储敏感配置。
/// </summary>
public class KeyVaultEntity
{
    [Key]
    public long Id { get; set; }

    [MaxLength(64)]
    public string KeyVaultId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Description { get; set; }

    public string EncryptedValue { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Category { get; set; } = "general";

    public string? Tags { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
