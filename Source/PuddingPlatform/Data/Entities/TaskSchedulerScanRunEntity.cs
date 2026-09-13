using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// P1-B Scan Run 持久化：task_scheduler_scan_runs 表实体 — 工作区级调度扫描摘要审计。
/// <para>
/// 列集合 = 实施方案 §7.1 DDL（Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md）。
/// status 用 wire 字符串 running/succeeded/failed/abandoned（见
/// <see cref="PuddingPlatform.Services.Scheduling.TaskSchedulerScanRunStatuses"/>）；
/// started_at_utc / completed_at_utc 存固定宽度 UTC ISO-8601 TEXT，与
/// task_scheduler_intents 系列表的时间序约定一致。
/// 存储走 <see cref="PuddingPlatform.Services.Scheduling.TaskSchedulerScanRunStore"/> 原生 SQL
/// （与 decisions/outcomes 同风格，刻意不注册 EF 模型），本实体只承担列契约与文档。
/// </para>
/// </summary>
[Table("task_scheduler_scan_runs")]
public sealed class TaskSchedulerScanRunEntity
{
    [Key, Column("scan_id")]
    public string ScanId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("trigger")]
    public string Trigger { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("mode")]
    public string Mode { get; set; } = string.Empty;

    [Required, Column("policy_revision")]
    public int PolicyRevision { get; set; }

    [Required, MaxLength(128), Column("host_boot_id")]
    public string HostBootId { get; set; } = string.Empty;

    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "running";

    [Required, Column("started_at_utc")]
    public string StartedAtUtc { get; set; } = string.Empty;

    [Column("completed_at_utc")]
    public string? CompletedAtUtc { get; set; }

    [Column("duration_ms")]
    public long? DurationMs { get; set; }

    [Required, Column("availability_refreshed")]
    public int AvailabilityRefreshed { get; set; }

    [Required, Column("idle_agents")]
    public int IdleAgents { get; set; }

    [Required, Column("busy_agents")]
    public int BusyAgents { get; set; }

    [Required, Column("unknown_agents")]
    public int UnknownAgents { get; set; }

    [Required, Column("backlog")]
    public int Backlog { get; set; }

    [Required, Column("candidates")]
    public int Candidates { get; set; }

    [Required, Column("eligible")]
    public int Eligible { get; set; }

    [Required, Column("started")]
    public int Started { get; set; }

    [Required, Column("tracked")]
    public int Tracked { get; set; }

    [Required, Column("repaired")]
    public int Repaired { get; set; }

    [Column("decision_codes_json")]
    public string? DecisionCodesJson { get; set; }

    [Column("repair_codes_json")]
    public string? RepairCodesJson { get; set; }

    [MaxLength(64), Column("error_code")]
    public string? ErrorCode { get; set; }

    [Column("error_summary")]
    public string? ErrorSummary { get; set; }
}
