using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>Intent 结算 outcome 的稳定枚举（实施方案 §3.2；failed 仅由 FailAsync 状态位承载，不落本表）。</summary>
public static class TaskSchedulerIntentOutcomes
{
    /// <summary>事务已创建新的 fenced Assignment/Reservation/Binding/Goal。</summary>
    public const string Started = "started";

    /// <summary>有稳定 defer code 和 nextEligibleAtUtc，等待下一轮。</summary>
    public const string Deferred = "deferred";

    /// <summary>策略、能力、窗口或版本明确拒绝。</summary>
    public const string Denied = "denied";

    /// <summary>触发 Task 不在可评估状态或未 opt-in。</summary>
    public const string Ineligible = "ineligible";

    /// <summary>Task 已终态，无需调度。</summary>
    public const string Terminal = "terminal";

    /// <summary>非 Task 级事件只造成 Availability 刷新，结果已持久化。</summary>
    public const string Noop = "noop";
}

/// <summary>task_scheduler_intent_outcomes 单行投影（写与读共用；PK intent_id 保证幂等）。</summary>
public sealed record TaskSchedulerIntentOutcomeRecord
{
    public required string IntentId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? TaskId { get; init; }
    public required string Outcome { get; init; }
    public string? DecisionId { get; init; }
    public required string ScanId { get; init; }
    public int PolicyRevision { get; init; }
    public required string OptionsHash { get; init; }
    public string? ReasonCode { get; init; }
    public string? StartedAssignmentId { get; init; }
    public string? StartedGoalRunId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public interface ITaskSchedulerIntentOutcomeStore
{
    /// <summary>
    /// 批量 durable 写入（单事务 + INSERT OR IGNORE，PK intent_id 幂等：同 Intent 重放不产生重复行）。
    /// 任何行失败则整个批次回滚——authoritative 结算路径靠此保证「要么全部 outcome 可见，要么零写入重试」。
    /// 返回新插入行数（重放时为 0）。
    /// </summary>
    Task<int> RecordAsync(IReadOnlyList<TaskSchedulerIntentOutcomeRecord> outcomes, CancellationToken ct = default);

    /// <summary>该 Intent 是否已有 durable outcome（crash 后 replay 的幂等检查点）。</summary>
    Task<bool> HasOutcomeAsync(string intentId, CancellationToken ct = default);

    /// <summary>读取单个 Intent 的 outcome；不存在返回 null。</summary>
    Task<TaskSchedulerIntentOutcomeRecord?> GetOutcomeAsync(string intentId, CancellationToken ct = default);
}

/// <summary>
/// task_scheduler_intent_outcomes 的 SQLite 实现。刻意走原生 SQL（不注册 EF 实体），
/// 与 <see cref="TaskSchedulerIntentStore"/> / <see cref="TaskSchedulerDecisionStore"/> 同风格；
/// 时间列存固定宽度 UTC ISO-8601 TEXT。原生 SQL 用 {n} 复合占位符 + object[] 走 EF 参数化重载。
/// </summary>
public sealed class TaskSchedulerIntentOutcomeStore(
    IDbContextFactory<PlatformDbContext> dbFactory) : ITaskSchedulerIntentOutcomeStore
{
    public async Task<int> RecordAsync(IReadOnlyList<TaskSchedulerIntentOutcomeRecord> outcomes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = 0;
        foreach (var outcome in outcomes)
        {
            ct.ThrowIfCancellationRequested();
            inserted += await db.Database.ExecuteSqlAsync(
                $"""
                INSERT OR IGNORE INTO task_scheduler_intent_outcomes
                    (intent_id, workspace_id, task_id, outcome, decision_id, scan_id,
                     policy_revision, options_hash, reason_code, started_assignment_id,
                     started_goal_run_id, created_at_utc)
                VALUES (
                    {outcome.IntentId},
                    {outcome.WorkspaceId},
                    {outcome.TaskId},
                    {outcome.Outcome},
                    {outcome.DecisionId},
                    {outcome.ScanId},
                    {outcome.PolicyRevision},
                    {outcome.OptionsHash},
                    {outcome.ReasonCode},
                    {outcome.StartedAssignmentId},
                    {outcome.StartedGoalRunId},
                    {Format(outcome.CreatedAtUtc)})
                """, ct);
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<bool> HasOutcomeAsync(string intentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var count = await db.Database.SqlQuery<long>(
            $"""
            SELECT COUNT(*) AS Value
            FROM task_scheduler_intent_outcomes
            WHERE intent_id = {intentId}
            """).SingleAsync(ct);
        return count > 0;
    }

    public async Task<TaskSchedulerIntentOutcomeRecord?> GetOutcomeAsync(string intentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.SqlQuery<TaskSchedulerIntentOutcomeRecord>(
            $"""
            SELECT intent_id AS IntentId, workspace_id AS WorkspaceId, task_id AS TaskId,
                   outcome AS Outcome, decision_id AS DecisionId, scan_id AS ScanId,
                   policy_revision AS PolicyRevision, options_hash AS OptionsHash,
                   reason_code AS ReasonCode, started_assignment_id AS StartedAssignmentId,
                   started_goal_run_id AS StartedGoalRunId, created_at_utc AS CreatedAtUtc
            FROM task_scheduler_intent_outcomes
            WHERE intent_id = {intentId}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
    }

    private static string Format(DateTimeOffset value) => value
        .ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
