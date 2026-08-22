using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-075: 第三方 External Access Token 主表。
/// 只保存 canonical token 的 SHA-256 摘要（secret_hash BLOB(32)）；明文仅出现在创建响应中一次。
/// 列名与 ExternalAccessTokenSchemaBootstrapper 的 DDL 严格一致。
/// </summary>
[Table("external_access_tokens")]
public class ExternalAccessTokenEntity
{
    [Key, Required, MaxLength(64), Column("token_id")]
    public string TokenId { get; set; } = string.Empty;

    /// <summary>Header 中的公开定位符（Base64Url 128-bit），只用于 O(1) 查询，不作为认证证据。</summary>
    [Required, MaxLength(64), Column("key_id")]
    public string KeyId { get; set; } = string.Empty;

    /// <summary>canonical token SHA-256（32 字节）。</summary>
    [Required, Column("secret_hash")]
    public byte[] SecretHash { get; set; } = [];

    /// <summary>安全显示前缀（如 pdt_v1_abc12345…），不含 Secret。</summary>
    [Required, MaxLength(32), Column("display_prefix")]
    public string DisplayPrefix { get; set; } = string.Empty;

    [Required, MaxLength(100), Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>创建 Token 的 Admin UserId；owner 禁用/删除时 Token fail closed。</summary>
    [Required, MaxLength(64), Column("owner_user_id")]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>管理操作 CAS 版本。</summary>
    [Required, Column("version")]
    public int Version { get; set; } = 1;

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("expires_at_utc")]
    public DateTimeOffset ExpiresAtUtc { get; set; }

    [Column("revoked_at_utc")]
    public DateTimeOffset? RevokedAtUtc { get; set; }

    [MaxLength(64), Column("revoked_by_user_id")]
    public string? RevokedByUserId { get; set; }

    [MaxLength(500), Column("revocation_reason")]
    public string? RevocationReason { get; set; }

    /// <summary>合并写入的近似最后使用时间（最多 5 分钟误差），不在认证热路径更新。</summary>
    [Column("last_used_at_utc")]
    public DateTimeOffset? LastUsedAtUtc { get; set; }

    public ICollection<ExternalAccessTokenScopeEntity> Scopes { get; set; } = [];
    public ICollection<ExternalAccessTokenWorkspaceEntity> Workspaces { get; set; } = [];
}
