using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

[Table("task_dependencies")]
public sealed class TaskDependencyEntity
{
    [Key, Required, MaxLength(64), Column("dependency_id")]
    public string DependencyId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("predecessor_task_id")]
    public string PredecessorTaskId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("successor_task_id")]
    public string SuccessorTaskId { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("kind")]
    public string Kind { get; set; } = "finish_to_start";

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}
