using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 会话事件日志实体 — append-only 不可变记录。
/// 存储 Agent 执行过程中的每一帧（delta/thinking/tool_call/subagent/done...）。
/// 
/// 关联 ADR：Docs/07架构/16会话状态层与客户端解耦ADR.md §4.1
/// </summary>
[Table("session_event_log")]
public class SessionEventLogEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Column("sequence_num")]
    public long SequenceNum { get; set; }

    [Required, MaxLength(64), Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [Required, Column("data")]
    public string Data { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("recorded_at")]
    public string RecordedAt { get; set; } = string.Empty;

    [MaxLength(64), Column("trace_id")]
    public string? TraceId { get; set; }

    [MaxLength(64), Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [MaxLength(64), Column("execution_id")]
    public string? ExecutionId { get; set; }

    [MaxLength(64), Column("parent_execution_id")]
    public string? ParentExecutionId { get; set; }

    [MaxLength(64), Column("sub_agent_id")]
    public string? SubAgentId { get; set; }

    [MaxLength(64), Column("component")]
    public string? Component { get; set; }

    [MaxLength(128), Column("operation")]
    public string? Operation { get; set; }
}
