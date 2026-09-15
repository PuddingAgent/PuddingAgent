using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-092 §5.1: goal_acceptance_contracts — 版本化验收合同（必需条件 + 版本化检查定义）。
/// <para>
/// 一个 (goal_run_id, activation_epoch, objective_version) 对应一行；合同缺失或
/// criteria_json 为空代表"尚未派生合同"，Verifier 必须按 acceptance_contract_missing
/// 走有界修复步骤，不得 vacuous pass，也不得用 Task.Status==Completed 代理验收。
/// </para>
/// </summary>
[Table("goal_acceptance_contracts")]
public class GoalAcceptanceContractEntity
{
    [Key, Required, MaxLength(160), Column("contract_id")]
    public string ContractId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("goal_run_id")]
    public string GoalRunId { get; set; } = string.Empty;

    [Required, Column("activation_epoch")]
    public int ActivationEpoch { get; set; }

    [Required, Column("objective_version")]
    public int ObjectiveVersion { get; set; }

    /// <summary>合同自身的修订号：条件/检查定义变化时必须递增。</summary>
    [Required, Column("contract_version")]
    public int ContractVersion { get; set; } = 1;

    [MaxLength(128), Column("plan_fingerprint")]
    public string? PlanFingerprint { get; set; }

    [Required, Column("criteria_json")]
    public string CriteriaJson { get; set; } = "[]";

    [Required, Column("checks_json")]
    public string ChecksJson { get; set; } = "[]";

    /// <summary>bounded_planning | planner | repair。</summary>
    [Required, MaxLength(32), Column("source")]
    public string Source { get; set; } = "bounded_planning";

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
