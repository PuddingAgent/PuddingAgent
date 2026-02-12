using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 子代理状态追踪实体。
/// 追踪异步子代理从 spawned → running → completed/failed 的生命周期。
/// 
/// 关联 ADR：Docs/07架构/16会话状态层与客户端解耦ADR.md §4.2
/// </summary>
[Table("session_sub_agents")]
public class SessionSubAgentEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("parent_session_id")]
    public string ParentSessionId { get; set; } = string.Empty;

    [MaxLength(64), Column("parent_agent_id")]
    public string? ParentAgentId { get; set; }

    [Required, MaxLength(64), Column("sub_session_id")]
    public string SubSessionId { get; set; } = string.Empty;

    [Required, MaxLength(16)]
    public string Status { get; set; } = "running";

    [MaxLength(64), Column("template_id")]
    public string? TemplateId { get; set; }

    [MaxLength(128), Column("model_id")]
    public string? ModelId { get; set; }

    [Required, Column("task_summary")]
    public string TaskSummary { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("spawned_at")]
    public string SpawnedAt { get; set; } = string.Empty;

    [MaxLength(32), Column("completed_at")]
    public string? CompletedAt { get; set; }

    public bool? Success { get; set; }

    [MaxLength(256), Column("reply_summary")]
    public string? ReplySummary { get; set; }

    [MaxLength(512), Column("error_summary")]
    public string? ErrorSummary { get; set; }

    [Column("full_result_json")]
    public string? FullResultJson { get; set; }
}
