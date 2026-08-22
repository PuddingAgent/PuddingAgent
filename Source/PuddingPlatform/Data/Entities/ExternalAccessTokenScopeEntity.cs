using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-075: Token scope 联结表。联合主键 (token_id, scope)；scope 只允许服务端白名单精确值。
/// scope 与 workspace 不可在创建后原地扩大；需要更大权限时创建新 Token。
/// </summary>
[Table("external_access_token_scopes")]
public class ExternalAccessTokenScopeEntity
{
    [Required, MaxLength(64), Column("token_id")]
    public string TokenId { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("scope")]
    public string Scope { get; set; } = string.Empty;
}
