using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// P0 Scheduler 事件驱动层：task_scheduler_intents 表实体 — durable 调度意图队列。
/// <para>
/// 幂等键 UNIQUE(source, source_event_id)（账本行最多产生一个 intent）；
/// status 用 wire 字符串 pending/processing/done/dead；lease_until_utc / created_at_utc /
/// processed_at_utc 存固定宽度 UTC ISO-8601 TEXT，保证 SQLite 原生 SQL 字典序比较与
/// 排序和时间序一致（EF SQLite 无法翻译 DateTimeOffset 比较，见 evaluator 注释）。
/// </para>
/// </summary>
[Table("task_scheduler_intents")]
public sealed class TaskSchedulerIntentEntity
{
    [Key, Column("intent_id")]
    public string IntentId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("source")]
    public string Source { get; set; } = string.Empty;

    [Required, Column("source_event_id")]
    public long SourceEventId { get; set; }

    [Required, MaxLength(64), Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [MaxLength(64), Column("task_id")]
    public string? TaskId { get; set; }

    [MaxLength(64), Column("goal_run_id")]
    public string? GoalRunId { get; set; }

    [Column("payload_json")]
    public string? PayloadJson { get; set; }

    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "pending";

    [Required, Column("attempt_count")]
    public int AttemptCount { get; set; }

    [MaxLength(128), Column("lease_owner")]
    public string? LeaseOwner { get; set; }

    [Column("lease_until_utc")]
    public string? LeaseUntilUtc { get; set; }

    [Required, Column("created_at_utc")]
    public string CreatedAtUtc { get; set; } = string.Empty;

    [Column("processed_at_utc")]
    public string? ProcessedAtUtc { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }
}
