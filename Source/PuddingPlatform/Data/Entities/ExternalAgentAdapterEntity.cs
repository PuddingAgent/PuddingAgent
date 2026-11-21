using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

[Table("external_agent_adapters")]
public sealed class ExternalAgentAdapterEntity
{
    [Key, Column("token_id")]
    public required string TokenId { get; set; }

    [Column("workspace_id")]
    public required string WorkspaceId { get; set; }

    [Column("agent_id")]
    public required string AgentId { get; set; }

    [Column("registered_at_utc")]
    public DateTimeOffset? RegisteredAtUtc { get; set; }
}
