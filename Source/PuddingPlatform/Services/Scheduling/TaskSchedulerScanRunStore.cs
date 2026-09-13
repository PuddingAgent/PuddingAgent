using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>scan run 生命周期的 wire 状态（实施方案 §7.1 status 列）。</summary>
public static class TaskSchedulerScanRunStatuses
{
    /// <summary>ScanRunner 已开始，summary 未落。</summary>
    public const string Running = "running";

    /// <summary>扫描完成（含 candidates=0 的空扫描），完整 summary 已持久化。</summary>
    public const string Succeeded = "succeeded";

    /// <summary>扫描异常终止，error_code/error_summary 已持久化。</summary>
    public const string Failed = "failed";

    /// <summary>启动恢复发现的上一次进程遗留 running（host_boot_id 不属于当前 boot）。</summary>
    public const string Abandoned = "abandoned";
}

/// <summary>task_scheduler_scan_runs 单行投影（GetLatestAsync 读回用）。</summary>
public sealed record TaskSchedulerScanRunRecord
{
    public required string ScanId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Trigger { get; init; }
    public required string Mode { get; init; }
    public int PolicyRevision { get; init; }
    public required string HostBootId { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public int AvailabilityRefreshed { get; init; }
    public int IdleAgents { get; init; }
    public int BusyAgents { get; init; }
    public int UnknownAgents { get; init; }
    public int Backlog { get; init; }
    public int Candidates { get; init; }
    public int Eligible { get; init; }
    public int Started { get; init; }
    public int Tracked { get; init; }
    public int Repaired { get; init; }
    public string? DecisionCodesJson { get; init; }
    public string? RepairCodesJson { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorSummary { get; init; }
}

/// <summary>扫描成功终态 summary（完成时间 + DDL 全部计数列 + 稳定原因分布）。</summary>
public sealed record TaskSchedulerScanRunCompletion
{
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required long DurationMs { get; init; }
    public int AvailabilityRefreshed { get; init; }
    public int IdleAgents { get; init; }
    public int BusyAgents { get; init; }
    public int UnknownAgents { get; init; }
    public int Backlog { get; init; }
    public int Candidates { get; init; }
    public int Eligible { get; init; }
    public int Started { get; init; }
    public int Tracked { get; init; }
    public int Repaired { get; init; }
    public string? DecisionCodesJson { get; init; }
    public string? RepairCodesJson { get; init; }
}

/// <summary>
/// task_scheduler_scan_runs 的 SQLite 实现：扫描持久化审计（P1-B §7.1/§7.2）。
/// 刻意走原生 SQL（不注册 EF 实体），与 <see cref="TaskSchedulerDecisionStore"/> /
/// <see cref="TaskSchedulerIntentOutcomeStore"/> 同风格；插值 SQL 由 EF 自动参数化。
/// 时间列存固定宽度 UTC ISO-8601 TEXT。
/// </summary>
public sealed class TaskSchedulerScanRunStore(IDbContextFactory<PlatformDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 进程级 host boot 标识：每次进程启动重新生成（PID + 随机段），
    /// <see cref="MarkAbandonedAsync"/> 据此把非当前 boot 遗留的 running 行判为 abandoned。
    /// </summary>
    public static string HostBootId { get; } =
        $"host-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..12]}";

    /// <summary>
    /// §7.2-1：扫描开始时插入 running 行，返回 scanId（后续 Complete/Fail 按 scanId 定位）。
    /// PK scan_id，开始失败直接抛出——DB 不可用时调度本就无法评估，不留无主 running 行。
    /// </summary>
    public async Task<string> TryStartAsync(
        string workspaceId,
        string trigger,
        string mode,
        int policyRevision,
        string hostBootId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostBootId);

        var scanId = $"scan-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO task_scheduler_scan_runs
                (scan_id, workspace_id, "trigger", mode, policy_revision, host_boot_id,
                 status, started_at_utc)
            VALUES ({scanId}, {workspaceId}, {trigger}, {mode}, {policyRevision}, {hostBootId},
                    {TaskSchedulerScanRunStatuses.Running}, {Format(DateTimeOffset.UtcNow)})
            """, ct);
        return scanId;
    }

    /// <summary>
    /// §7.2-2：成功终态。空扫描（candidates=0）同样适用；按 scan_id UPDATE，天然幂等不产生重复行。
    /// </summary>
    public async Task CompleteAsync(string scanId, TaskSchedulerScanRunCompletion completion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scanId);
        ArgumentNullException.ThrowIfNull(completion);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE task_scheduler_scan_runs
            SET status = {TaskSchedulerScanRunStatuses.Succeeded},
                completed_at_utc = {Format(completion.CompletedAtUtc)},
                duration_ms = {completion.DurationMs},
                availability_refreshed = {completion.AvailabilityRefreshed},
                idle_agents = {completion.IdleAgents},
                busy_agents = {completion.BusyAgents},
                unknown_agents = {completion.UnknownAgents},
                backlog = {completion.Backlog},
                candidates = {completion.Candidates},
                eligible = {completion.Eligible},
                started = {completion.Started},
                tracked = {completion.Tracked},
                repaired = {completion.Repaired},
                decision_codes_json = {completion.DecisionCodesJson},
                repair_codes_json = {completion.RepairCodesJson}
            WHERE scan_id = {scanId}
            """, ct);
    }

    /// <summary>§7.2-3：失败终态。调用方（ScanRunner）负责之后重新抛出原异常，本方法不吞错。</summary>
    public async Task FailAsync(string scanId, string errorCode, string errorSummary, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE task_scheduler_scan_runs
            SET status = {TaskSchedulerScanRunStatuses.Failed},
                completed_at_utc = {Format(DateTimeOffset.UtcNow)},
                error_code = {errorCode},
                error_summary = {errorSummary}
            WHERE scan_id = {scanId}
            """, ct);
    }

    /// <summary>
    /// §7.2-4：启动恢复——把 host_boot_id ≠ 当前 boot 的遗留 running 行标记 abandoned，
    /// 返回影响行数。时间事实以判定时刻为准（completed_at_utc=now，duration 不可知留 NULL）。
    /// </summary>
    public async Task<int> MarkAbandonedAsync(string hostBootId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostBootId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE task_scheduler_scan_runs
            SET status = {TaskSchedulerScanRunStatuses.Abandoned},
                completed_at_utc = {Format(DateTimeOffset.UtcNow)}
            WHERE status = {TaskSchedulerScanRunStatuses.Running}
              AND host_boot_id <> {hostBootId}
            """, ct);
    }

    /// <summary>状态 API 数据源：该工作区最后一轮扫描（含 running/failed，按 started_at_utc DESC）。</summary>
    public async Task<TaskSchedulerScanRunRecord?> GetLatestAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.SqlQuery<TaskSchedulerScanRunRecord>(
            $"""
            SELECT scan_id AS ScanId, workspace_id AS WorkspaceId, "trigger" AS Trigger,
                   mode AS Mode, policy_revision AS PolicyRevision, host_boot_id AS HostBootId,
                   status AS Status, started_at_utc AS StartedAtUtc,
                   completed_at_utc AS CompletedAtUtc, duration_ms AS DurationMs,
                   availability_refreshed AS AvailabilityRefreshed, idle_agents AS IdleAgents,
                   busy_agents AS BusyAgents, unknown_agents AS UnknownAgents,
                   backlog AS Backlog, candidates AS Candidates, eligible AS Eligible,
                   started AS Started, tracked AS Tracked, repaired AS Repaired,
                   decision_codes_json AS DecisionCodesJson, repair_codes_json AS RepairCodesJson,
                   error_code AS ErrorCode, error_summary AS ErrorSummary
            FROM task_scheduler_scan_runs
            WHERE workspace_id = {workspaceId}
            ORDER BY started_at_utc DESC
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
    }

    /// <summary>稳定原因分布 → JSON（§7.2-5 只存汇总分布，不双写明细）。</summary>
    public static string SerializeCodeDistribution(IReadOnlyDictionary<string, int> codes) =>
        JsonSerializer.Serialize(codes, JsonOptions);

    private static string Format(DateTimeOffset value) => value
        .ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
