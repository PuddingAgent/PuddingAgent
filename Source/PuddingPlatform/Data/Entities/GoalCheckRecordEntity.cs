using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-092 §6.2: goal_check_records — verification/check 的持久工作项、租约与真实运行报告。
/// <para>
/// 生命周期 pending → leased → finished；DedupKey = scope|criterionRevision|definitionHash|inputFingerprint，
/// 同一去重键只允许一行（重复声明复用既有记录，不重复执行）。
/// 只有 <see cref="Status"/> == "finished" 且 <see cref="ReportJson"/> 非空的记录才能构成
/// 可用的检查报告；pending/leased/租约过期一律不得计入通过。
/// </para>
/// </summary>
[Table("goal_check_records")]
public class GoalCheckRecordEntity
{
    [Key, Required, MaxLength(192), Column("check_record_id")]
    public string CheckRecordId { get; set; } = string.Empty;

    [Required, MaxLength(288), Column("dedup_key")]
    public string DedupKey { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("goal_run_id")]
    public string GoalRunId { get; set; } = string.Empty;

    [Required, Column("activation_epoch")]
    public int ActivationEpoch { get; set; }

    [Required, Column("iteration_no")]
    public int IterationNo { get; set; }

    [Required, MaxLength(64), Column("check_id")]
    public string CheckId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("criterion_id")]
    public string CriterionId { get; set; } = string.Empty;

    [Required, Column("criterion_revision")]
    public int CriterionRevision { get; set; }

    [MaxLength(128), Column("definition_hash")]
    public string? DefinitionHash { get; set; }

    [MaxLength(128), Column("input_fingerprint")]
    public string? InputFingerprint { get; set; }

    /// <summary>pending | leased | finished。</summary>
    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "pending";

    [MaxLength(128), Column("lease_owner")]
    public string? LeaseOwner { get; set; }

    [Column("lease_until_utc")]
    public DateTimeOffset? LeaseUntilUtc { get; set; }

    [Required, Column("attempt")]
    public int Attempt { get; set; }

    [Required, Column("priority")]
    public int Priority { get; set; }

    /// <summary>finished 时的 GoalCheckReport JSON；为空表示没有可信报告。</summary>
    [Column("report_json")]
    public string? ReportJson { get; set; }

    [MaxLength(64), Column("failure_code")]
    public string? FailureCode { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
