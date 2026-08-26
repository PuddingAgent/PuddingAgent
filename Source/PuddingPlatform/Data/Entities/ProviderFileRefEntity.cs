using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-077 §6.2：<c>llm_provider_file_refs</c> 表实体。
/// 全原始 SQL store（<see cref="PuddingPlatform.Services.Files.SqliteProviderFileRefStore"/>）
/// 与 EF EnsureCreated 共用本定义；列名/类型与 <see cref="PuddingCode.Core.ProviderFileRefRecord"/> 严格一致。
/// <c>remote_file_id</c>（provider file_id）**不**出现在普通日志/异常/诊断包（ADR-077 §8）。
/// </summary>
[Table("llm_provider_file_refs")]
public sealed class ProviderFileRefEntity
{
    /// <summary>LLM provider 标识（如 deepseek）。</summary>
    [Key, Required, MaxLength(64), Column("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>凭据轮次（credential epoch；Secret 本身永不入库）。</summary>
    [Key, Required, MaxLength(64), Column("credential_epoch")]
    public string CredentialEpoch { get; set; } = string.Empty;

    /// <summary>本地 Artifact 永久身份（不随 provider 文件生命周期变化）。</summary>
    [Required, MaxLength(64), Column("artifact_id")]
    public string ArtifactId { get; set; } = string.Empty;

    /// <summary>Artifact 内容 SHA-256（十六进制小写）。</summary>
    [Key, Required, MaxLength(64), Column("artifact_sha256")]
    public string ArtifactSha256 { get; set; } = string.Empty;

    /// <summary>Provider 侧远端 file_id（唯一凭证；不入日志）。</summary>
    [Required, MaxLength(128), Column("remote_file_id")]
    public string RemoteFileId { get; set; } = string.Empty;

    /// <summary>原始字节数。</summary>
    [Required, Column("bytes")]
    public long Bytes { get; set; }

    /// <summary>MIME 类型（image/jpeg 等）。</summary>
    [Required, MaxLength(64), Column("mime_type")]
    public string MimeType { get; set; } = string.Empty;

    /// <summary>有效期截止（UTC；"O" 格式 TEXT）。</summary>
    [Required, Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>最近一次复用时间（UTC；可空）。</summary>
    [Column("last_used_at")]
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>wire 状态（uploading/ready/delete_pending/expired/failed）。</summary>
    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = "uploading";

    [Required, Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Required, Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}
