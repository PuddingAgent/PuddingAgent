using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>ADR-075: Token 安全审计事件类型（冻结白名单）。</summary>
public enum ExternalAccessTokenAuditEventType
{
    Created = 0,
    Renamed = 1,
    Revoked = 2,
    AuthenticationSucceeded = 3,
    AuthenticationFailed = 4,
    ScopeDenied = 5,
    WorkspaceDenied = 6,
}

/// <summary>
/// ADR-075: Token 安全审计事实。只保存 token_id/key_id，不保存 token 明文或摘要；
/// authentication_failed 按原因类别聚合（仅记录 keyId 可定位的已知 Token，未知 keyId 走限速日志）。
/// </summary>
[Table("external_access_token_audit_events")]
public class ExternalAccessTokenAuditEventEntity
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required, Column("event_id")]
    public string EventId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("token_id")]
    public string TokenId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("key_id")]
    public string KeyId { get; set; } = string.Empty;

    [Required, Column("event_type")]
    public ExternalAccessTokenAuditEventType EventType { get; set; }

    /// <summary>失败原因类别（malformed/unknown_key/bad_secret/revoked/expired/owner_disabled）或其他 detail。</summary>
    [MaxLength(64), Column("reason")]
    public string? Reason { get; set; }

    /// <summary>操作发起者（Admin UserId 或 external actor）。</summary>
    [MaxLength(128), Column("actor")]
    public string? Actor { get; set; }

    [Required, Column("occurred_at_utc")]
    public DateTimeOffset OccurredAtUtc { get; set; }
}
