using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PuddingCode.Tasks;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// TB-02: workspace_tasks 表实体 — 对应 PuddingCode.Tasks.WorkspaceTask。
/// 枚举存 int、时间存 DateTimeOffset、列名 snake_case。
/// </summary>
[Table("workspace_tasks")]
public class WorkspaceTaskEntity
{
    [Key, Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("acceptance_criteria")]
    public string? AcceptanceCriteria { get; set; }

    [Required, Column("status")]
    public WorkspaceTaskStatus Status { get; set; }

    [Required, Column("priority")]
    public TaskPriority Priority { get; set; }

    [Required, Column("execution_window")]
    public TaskExecutionWindow ExecutionWindow { get; set; }

    [MaxLength(64), Column("preferred_agent_id")]
    public string? PreferredAgentId { get; set; }

    [Required, MaxLength(64), Column("task_type")]
    public string TaskType { get; set; } = "general";

    [Required, Column("required_capabilities_json")]
    public string RequiredCapabilitiesJson { get; set; } = "[]";

    [MaxLength(64), Column("required_provider_id")]
    public string? RequiredProviderId { get; set; }

    [MaxLength(128), Column("required_model_id")]
    public string? RequiredModelId { get; set; }

    [Required, Column("allow_agent_fallback")]
    public bool AllowAgentFallback { get; set; }

    [Required, Column("auto_dispatch_enabled")]
    public bool AutoDispatchEnabled { get; set; }

    [MaxLength(64), Column("active_assignment_id")]
    public string? ActiveAssignmentId { get; set; }

    [Column("not_before_utc")]
    public DateTimeOffset? NotBeforeUtc { get; set; }

    [Column("due_at_utc")]
    public DateTimeOffset? DueAtUtc { get; set; }

    [Column("next_eligible_at_utc")]
    public DateTimeOffset? NextEligibleAtUtc { get; set; }

    [Required, Column("sort_order")]
    public long SortOrder { get; set; }

    [Column("progress_percent")]
    public int? ProgressPercent { get; set; }

    [Column("progress_summary")]
    public string? ProgressSummary { get; set; }

    [Column("blocker_kind")]
    public string? BlockerKind { get; set; }

    [Column("blocker_reason")]
    public string? BlockerReason { get; set; }

    [Column("failure_code")]
    public string? FailureCode { get; set; }

    [Column("failure_reason")]
    public string? FailureReason { get; set; }

    [Column("origin")]
    public TaskOrigin? Origin { get; set; }

    [Required, Column("version")]
    public int Version { get; set; } = 1;

    [Column("created_by")]
    public string? CreatedBy { get; set; }

    [Column("updated_by")]
    public string? UpdatedBy { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [Column("completed_at_utc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }

    [Column("failed_at_utc")]
    public DateTimeOffset? FailedAtUtc { get; set; }

    [Column("archived_at_utc")]
    public DateTimeOffset? ArchivedAtUtc { get; set; }
}
