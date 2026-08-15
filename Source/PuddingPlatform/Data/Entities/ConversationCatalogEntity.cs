using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// P0-4f: Conversation catalog — materialized projection row summarising each conversation.
/// </summary>
[Table("conversation_catalog")]
public class ConversationCatalogEntity
{
    [Key, Required, MaxLength(64), Column("conversation_id")]
    public string ConversationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(64), Column("agent_id")]
    public string? AgentId { get; set; }

    [MaxLength(64), Column("principal_id")]
    public string? PrincipalId { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Required, Column("status")]
    public string Status { get; set; } = "active";

    [Required, Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [Required, Column("last_active_at")]
    public string LastActiveAt { get; set; } = string.Empty;

    [MaxLength(64), Column("parent_conversation_id")]
    public string? ParentConversationId { get; set; }

    [MaxLength(64), Column("successor_conversation_id")]
    public string? SuccessorConversationId { get; set; }
}