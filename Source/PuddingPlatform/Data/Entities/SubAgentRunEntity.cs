using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 子代理运行归档的数据库索引实体。
/// 文件系统为主存储（run.json / events.jsonl / tools.jsonl），此表仅做索引查询。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// </summary>
[Table("sub_agent_runs")]
public class SubAgentRunEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("run_id")]
    public string RunId { get; set; } = "";

    [Required, MaxLength(64), Column("parent_session_id")]
    public string ParentSessionId { get; set; } = "";

    [Required, MaxLength(64), Column("sub_session_id")]
    public string SubSessionId { get; set; } = "";

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = "";

    [Required, MaxLength(64), Column("agent_instance_id")]
    public string AgentInstanceId { get; set; } = "";

    [Required, MaxLength(64), Column("template_id")]
    public string TemplateId { get; set; } = "";

    /// <summary>运行状态：running / completed / failed / cancelled</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "running";

    [Required, MaxLength(32), Column("started_at")]
    public string StartedAt { get; set; } = "";

    [MaxLength(32), Column("completed_at")]
    public string? CompletedAt { get; set; }

    [Required, MaxLength(512), Column("archive_path")]
    public string ArchivePath { get; set; } = "";

    [MaxLength(64), Column("trace_id")]
    public string? TraceId { get; set; }

    [MaxLength(64), Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [MaxLength(1024), Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("task_planning_metadata_json")]
    public string? TaskPlanningMetadataJson { get; set; }

    [Column("total_rounds")]
    public int TotalRounds { get; set; }

    [Column("total_tool_calls")]
    public int TotalToolCalls { get; set; }

    [Column("total_duration_ms")]
    public long TotalDurationMs { get; set; }
}
