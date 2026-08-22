using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PuddingCode.Tasks;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-075: 追加式任务评价（task_evaluations）。不 UPDATE/DELETE 历史；
/// 更正通过 supersedes_evaluation_id 指向同 actor 旧评价。
/// </summary>
[Table("task_evaluations")]
public class TaskEvaluationEntity
{
    [Key, Required, MaxLength(64), Column("evaluation_id")]
    public string EvaluationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>accepted / needs_changes / rejected。</summary>
    [Required, MaxLength(32), Column("verdict")]
    public string Verdict { get; set; } = string.Empty;

    [Required, Column("score")]
    public int Score { get; set; }

    [Required, Column("comment")]
    public string Comment { get; set; } = string.Empty;

    /// <summary>评价者观察到的 Task version，防评价错误版本。</summary>
    [Required, Column("task_version_observed")]
    public int TaskVersionObserved { get; set; }

    [MaxLength(64), Column("supersedes_evaluation_id")]
    public string? SupersedesEvaluationId { get; set; }

    /// <summary>external_access_token / user / agent。</summary>
    [Required, MaxLength(32), Column("evaluator_type")]
    public string EvaluatorType { get; set; } = string.Empty;

    /// <summary>external actor 为 access-token:{tokenId}。</summary>
    [Required, MaxLength(128), Column("evaluator_id")]
    public string EvaluatorId { get; set; } = string.Empty;

    [Required, MaxLength(128), Column("evaluator_display_name")]
    public string EvaluatorDisplayName { get; set; } = string.Empty;

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}
