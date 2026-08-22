using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-075: External API mutation 幂等事实（external_api_idempotency）。
/// key = SHA-256(tokenId + method + canonical route + Idempotency-Key)；
/// 同 key 同 request hash 重放原资源；同 key 不同 hash 409；默认保留 7 天。
/// </summary>
[Table("external_api_idempotency")]
public class ExternalApiIdempotencyEntity
{
    /// <summary>复合作用域 key 的十六进制摘要（PK）。</summary>
    [Key, Required, MaxLength(64), Column("idempotency_key_hash")]
    public string IdempotencyKeyHash { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("token_id")]
    public string TokenId { get; set; } = string.Empty;

    /// <summary>请求 body 的 SHA-256 十六进制。</summary>
    [Required, MaxLength(64), Column("request_hash")]
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>原响应 HTTP 状态码（重放时复用）。</summary>
    [Required, Column("response_status")]
    public int ResponseStatus { get; set; }

    /// <summary>创建的资源 ID（task/comment/evaluation），重放时按它返回资源。</summary>
    [MaxLength(128), Column("resource_id")]
    public string? ResourceId { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}
