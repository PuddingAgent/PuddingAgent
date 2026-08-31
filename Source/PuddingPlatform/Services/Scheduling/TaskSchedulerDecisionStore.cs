using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Scheduling;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// 调度决策 durable 持久化（task_scheduler_decisions）。三个写点共享本 Store：
/// ① ScanRunner candidate 评估后每卡一行（shadow 与 authoritative 都落，mode 列区分）；
/// ② BacklogRefinement 5 种 verdict 落 phase=refinement 行；
/// ③ defer/deny 决策写回 workspace_tasks.next_eligible_at_utc（只前推不回拨）。
/// <para>
/// 幂等：UNIQUE(scan_id, task_id, phase) + INSERT OR IGNORE——同 scan 重放不产生重复行。
/// 刻意走原生 SQL（不注册 EF 实体），持久化失败不视为扫描失败——决策持久化是观测能力，
/// 由调用方决定是否吞错记日志；表结构由 <see cref="TaskSchedulerDecisionSchemaBootstrapper"/> 建立。
/// </para>
/// </summary>
public sealed class TaskSchedulerDecisionStore(
    IDbContextFactory<PlatformDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>candidate 决策落库，返回新插入行数（重放时为 0）。</summary>
    public async Task<int> RecordCandidateDecisionsAsync(
        string workspaceId,
        string mode,
        string scanId,
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanId);
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.Count == 0)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = 0;
        foreach (var decision in decisions)
        {
            ct.ThrowIfCancellationRequested();
            var breakdownJson = decision.ScoreBreakdown is null
                ? null
                : JsonSerializer.Serialize(decision.ScoreBreakdown, JsonOptions);
            var reason = BuildReason(decision);
            inserted += await db.Database.ExecuteSqlAsync(
                $"""
                INSERT OR IGNORE INTO task_scheduler_decisions
                    (decision_id, workspace_id, task_id, phase, mode, decision, decision_code,
                     reason, score_breakdown_json, next_eligible_at_utc, agent_id, scan_id, created_at_utc)
                VALUES (
                    {Guid.NewGuid().ToString("N")},
                    {workspaceId},
                    {decision.TaskId},
                    {"candidate"},
                    {mode},
                    {decision.Verdict.ToString().ToLowerInvariant()},
                    {TaskSchedulerDecisionCodes.Normalize(decision.Code)},
                    {reason},
                    {breakdownJson},
                    {decision.NextEligibleAtUtc?.ToString("O")},
                    {decision.AgentId},
                    {scanId},
                    {decision.EvaluatedAtUtc.ToString("O")})
                """, ct);
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    /// <summary>Backlog refinement verdict 落库（5 种 verdict 稳定 snake_case），返回新插入行数。</summary>
    public async Task<int> RecordRefinementDecisionsAsync(
        string workspaceId,
        string mode,
        string scanId,
        IReadOnlyList<TaskBacklogRefinementDecision> decisions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanId);
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.Count == 0)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = 0;
        foreach (var decision in decisions)
        {
            ct.ThrowIfCancellationRequested();
            var isReady = decision.Verdict == TaskBacklogRefinementVerdict.ReadyCandidate;
            var decisionState = isReady ? "ready" : "needs_refinement";
            inserted += await db.Database.ExecuteSqlAsync(
                $"""
                INSERT OR IGNORE INTO task_scheduler_decisions
                    (decision_id, workspace_id, task_id, phase, mode, decision, decision_code,
                     reason, score_breakdown_json, next_eligible_at_utc, agent_id, scan_id, created_at_utc)
                VALUES (
                    {Guid.NewGuid().ToString("N")},
                    {decision.WorkspaceId},
                    {decision.TaskId},
                    {"refinement"},
                    {mode},
                    {decisionState},
                    {TaskSchedulerDecisionCodes.Normalize(decision.Code)},
                    {decision.Code},
                    {null},
                    {null},
                    {decision.CompatibleAgentId},
                    {scanId},
                    {DateTimeOffset.UtcNow.ToString("O")})
                """, ct);
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    /// <summary>
    /// defer/deny 决策写回 workspace_tasks.next_eligible_at_utc（只前推不回拨），
    /// 返回实际更新行数。调度门写回不 bump Version、不动 updated_at——不是任务内容变更。
    /// </summary>
    public async Task<int> ApplyNextEligibleWriteBackAsync(
        string workspaceId,
        IReadOnlyList<TaskAutoDispatchCandidateDecision> decisions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(decisions);

        var targets = decisions
            .Where(item => item.NextEligibleAtUtc is not null
                && item.Verdict is not TaskAutoDispatchCandidateVerdict.Eligible)
            .GroupBy(item => item.TaskId, StringComparer.Ordinal)
            .Select(group => (
                TaskId: group.Key,
                NextEligible: group.Max(item => item.NextEligibleAtUtc!.Value)))
            .ToArray();
        if (targets.Length == 0)
            return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var updated = 0;
        foreach (var (taskId, nextEligible) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var existingText = await db.Database.SqlQuery<string?>(
                $"""
                SELECT next_eligible_at_utc AS Value
                FROM workspace_tasks
                WHERE workspace_id = {workspaceId}
                  AND task_id = {taskId}
                LIMIT 1
                """).FirstOrDefaultAsync(ct);
            // 只前推不回拨：现有门无法解析（外部写入格式）时直接写；现有门更晚则跳过。
            var shouldWrite = !DateTimeOffset.TryParse(existingText, out var existing)
                || nextEligible > existing;
            if (!shouldWrite)
                continue;
            updated += await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE workspace_tasks
                SET next_eligible_at_utc = {nextEligible.ToString("O")}
                WHERE workspace_id = {workspaceId}
                  AND task_id = {taskId}
                  AND (next_eligible_at_utc IS NULL
                       OR next_eligible_at_utc < {nextEligible.ToString("O")})
                """, ct);
        }

        return updated;
    }

    private static string BuildReason(TaskAutoDispatchCandidateDecision decision)
    {
        var parts = new List<string>(4)
        {
            $"code={TaskSchedulerDecisionCodes.Normalize(decision.Code)}",
        };
        if (decision.TaskType is not null)
            parts.Add($"task_type={decision.TaskType}");
        if (decision.DependencyState is not null)
            parts.Add($"dependency={decision.DependencyState}");
        if (decision.AvailabilityReason is not null)
            parts.Add($"availability={decision.AvailabilityReason}");
        if (decision.WindowCode is not null)
            parts.Add($"window={decision.WindowCode}");
        return string.Join(';', parts);
    }
}
