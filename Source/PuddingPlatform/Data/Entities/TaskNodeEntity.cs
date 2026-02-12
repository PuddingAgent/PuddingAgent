using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>任务规划树节点持久化实体。</summary>
[Table("task_nodes")]
public sealed class TaskNodeEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("task_node_id")]
    public string TaskNodeId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("plan_id")]
    public string PlanId { get; set; } = string.Empty;

    [MaxLength(64), Column("parent_task_node_id")]
    public string? ParentTaskNodeId { get; set; }

    [Column("depth")]
    public int Depth { get; set; }

    [MaxLength(256), Column("title")]
    public string? Title { get; set; }

    [MaxLength(2048), Column("objective")]
    public string? Objective { get; set; }

    [Column("input_context_summary")]
    public string? InputContextSummary { get; set; }

    [Column("expected_output_contract")]
    public string? ExpectedOutputContract { get; set; }

    [Required, MaxLength(32), Column("assigned_to_kind")]
    public string AssignedToKind { get; set; } = "Unassigned";

    [MaxLength(128), Column("assigned_to_id")]
    public string? AssignedToId { get; set; }

    [MaxLength(128), Column("assigned_template_id")]
    public string? AssignedTemplateId { get; set; }

    [MaxLength(64), Column("created_by_agent_id")]
    public string? CreatedByAgentId { get; set; }

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = "Draft";

    [Column("allow_sub_delegation")]
    public bool AllowSubDelegation { get; set; } = true;

    [Column("allow_agent_creation")]
    public bool AllowAgentCreation { get; set; } = true;

    [Column("result_summary")]
    public string? ResultSummary { get; set; }

    [Column("result_artifact_ref")]
    public string? ResultArtifactRef { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [MaxLength(64), Column("superseded_by_task_node_id")]
    public string? SupersededByTaskNodeId { get; set; }

    [Column("started_at")]
    public long? StartedAt { get; set; }

    [Column("completed_at")]
    public long? CompletedAt { get; set; }

    [Column("created_at")]
    public long CreatedAt { get; set; }

    [Column("updated_at")]
    public long UpdatedAt { get; set; }
}
