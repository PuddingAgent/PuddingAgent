using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// TB-05: task_dispatch_outbox 表实体 — 手工派发持久 Outbox（ADR-072 §11）。
/// <para>
/// 幂等键唯一（不变量 #4）；status 用 wire 字符串 pending/sent/failed/dead；
/// lease_until_utc 承载 Dispatcher 领取租约；枚举与时间用 snake_case 列名（同 TB-02 约定）。
/// </para>
/// </summary>
[Table("task_dispatch_outbox")]
public sealed class TaskDispatchOutboxEntity
{
    [Key, Column("id")]
    public long Id { get; set; }

    [Required, Column("idempotency_key")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("assignment_id")]
    public string AssignmentId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("origin")]
    public string Origin { get; set; } = string.Empty;

    /// <summary>JSON：<see cref="PuddingCode.Tasks.TaskInstructionEnvelope"/>。</summary>
    [Required, Column("envelope_payload")]
    public string EnvelopePayload { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("status")]
    public string Status { get; set; } = "pending";

    [Required, Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("lease_until_utc")]
    public DateTimeOffset? LeaseUntilUtc { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Column("sent_at_utc")]
    public DateTimeOffset? SentAtUtc { get; set; }
}
