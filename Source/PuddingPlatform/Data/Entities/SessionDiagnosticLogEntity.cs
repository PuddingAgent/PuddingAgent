using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 会话诊断日志 — 记录会话和子代理生命周期各阶段的时间戳和耗时。
/// 
/// 用途：前端 DevPanel / 管理面板的诊断视图。
/// 关联 ADR：Docs/07架构/16会话状态层与客户端解耦ADR.md
/// </summary>
[Table("session_diagnostic_log")]
public class SessionDiagnosticLogEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>关联的会话 ID（父会话或子会话）。</summary>
    [Required, MaxLength(64)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>关联的工作区 ID。</summary>
    [Required, MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>日志类别：session / sub_agent / event</summary>
    [Required, MaxLength(32)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// 阶段名称：
    ///   session: stream_started / first_token / tool_call_started / tool_call_completed / stream_completed / session_closed
    ///   sub_agent: spawned / execution_started / llm_called / tool_executed / completed / failed
    ///   event: appended / pushed_to_channel / persisted
    /// </summary>
    [Required, MaxLength(48)]
    public string Stage { get; set; } = string.Empty;

    /// <summary>关联的子代理会话 ID（仅 sub_agent 日志使用）。</summary>
    [MaxLength(64)]
    public string? SubSessionId { get; set; }

    /// <summary>阶段详细信息（JSON 或自由文本）。</summary>
    public string? Detail { get; set; }

    /// <summary>阶段耗时（毫秒），从上一个阶段到当前阶段。</summary>
    public long DurationMs { get; set; }

    /// <summary>累计耗时（毫秒），从会话创建到当前阶段。</summary>
    public long CumulativeMs { get; set; }

    /// <summary>ISO8601 UTC 时间戳。</summary>
    [Required, MaxLength(32)]
    public string RecordedAt { get; set; } = string.Empty;

    /// <summary>是否成功（true=正常，false=异常/失败。sub_agent 日志使用）。</summary>
    public bool? Success { get; set; }
}
