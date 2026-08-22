using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-075: Token workspace allow-list 联结表。联合主键 (token_id, workspace_id)；
/// 不支持全局通配符，创建时必须至少选择一个现存 workspace。
/// </summary>
[Table("external_access_token_workspaces")]
public class ExternalAccessTokenWorkspaceEntity
{
    [Required, MaxLength(64), Column("token_id")]
    public string TokenId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;
}
