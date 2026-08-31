using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>intent.source 取值（账本表名）。</summary>
public static class TaskSchedulerIntentSources
{
    public const string TaskEvents = "task_events";
    public const string ConversationEvents = "conversation_events";
}

/// <summary>intent.status wire 值。</summary>
public static class TaskSchedulerIntentStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Dead = "dead";
}

/// <summary>入队 envelope（账本桥构造；store 负责幂等落库）。</summary>
public sealed record TaskSchedulerIntentEnvelope
{
    public required string WorkspaceId { get; init; }
    public required string Source { get; init; }
    public required long SourceEventId { get; init; }
    public required string EventType { get; init; }
    public string? TaskId { get; init; }
    public string? GoalRunId { get; init; }
    public string? PayloadJson { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>被本 owner 成功领取的 intent 投影。</summary>
public sealed record TaskSchedulerIntent
{
    public required string IntentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Source { get; init; }
    public required long SourceEventId { get; init; }
    public required string EventType { get; init; }
    public string? TaskId { get; init; }
    public string? GoalRunId { get; init; }
    public string? PayloadJson { get; init; }
    public required int AttemptCount { get; init; }
}

public interface ITaskSchedulerIntentStore
{
    /// <summary>幂等入队：同 (source, source_event_id) 唯一冲突则忽略，返回是否新写入。</summary>
    Task<bool> EnqueueAsync(TaskSchedulerIntentEnvelope intent, CancellationToken ct = default);

    /// <summary>
    /// 事务内原子领取：pending，或 processing 但 lease 已过期的行，按 created_at_utc 排序
    /// 最多 batch 条，置 processing + 本 owner 租约，attempt_count+1。并发 Dequeue 互斥由
    /// 单条 UPDATE 的行锁语义保证（每个 intent 只被一个 owner 抢到）。
    /// </summary>
    Task<IReadOnlyList<TaskSchedulerIntent>> DequeueAsync(
        string workspaceId,
        int batch,
        TimeSpan lease,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>处理成功：标记 done 并清租约。</summary>
    Task CompleteAsync(string intentId, DateTimeOffset processedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// 处理失败：attempt_count（领取时已自增）达到 maxAttempts → dead，否则回 pending 清租约
    /// 等待重试。返回该 intent 是否已 dead。
    /// </summary>
    Task<bool> FailAsync(
        string intentId,
        string error,
        int maxAttempts,
        DateTimeOffset processedAtUtc,
        CancellationToken ct = default);

    /// <summary>账本尾游标恢复点：该来源已入队的 MAX(source_event_id)，空表返回 0。</summary>
    Task<long> GetTailCursorAsync(string source, CancellationToken ct = default);
}

/// <summary>
/// task_scheduler_intents 的 SQLite 实现。时间列存固定宽度 UTC ISO-8601 TEXT，
/// 原生 SQL 的字典序比较（lease 过期判定 / created_at_utc 排序）因此与时间序严格一致。
/// 原生 SQL 用 {n} 复合占位符 + object[] 走 EF 参数化重载（本 EF 版本无 FormattableString 异步重载）。
/// </summary>
public sealed class TaskSchedulerIntentStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<TaskSchedulerIntentStore> logger) : ITaskSchedulerIntentStore
{
    private readonly string _ownerId = $"task-scheduler-intent-{Environment.ProcessId}-{Guid.NewGuid():N}";

    public async Task<bool> EnqueueAsync(TaskSchedulerIntentEnvelope intent, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var intentId = Guid.NewGuid().ToString("N");
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT OR IGNORE INTO task_scheduler_intents
                (intent_id, workspace_id, source, source_event_id, event_type, task_id, goal_run_id,
                 payload_json, status, attempt_count, created_at_utc)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, 'pending', 0, {8})
            """,
            new object?[]
            {
                intentId,
                intent.WorkspaceId,
                intent.Source,
                intent.SourceEventId,
                intent.EventType,
                intent.TaskId,
                intent.GoalRunId,
                intent.PayloadJson,
                Format(intent.CreatedAtUtc),
            },
            ct);
        return affected > 0;
    }

    public async Task<IReadOnlyList<TaskSchedulerIntent>> DequeueAsync(
        string workspaceId,
        int batch,
        TimeSpan lease,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var leaseUntil = Format(now + lease);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE task_scheduler_intents
            SET status = 'processing', lease_owner = {0}, lease_until_utc = {1},
                attempt_count = attempt_count + 1
            WHERE intent_id IN (
                SELECT intent_id FROM task_scheduler_intents
                WHERE workspace_id = {2}
                  AND (status = 'pending'
                       OR (status = 'processing'
                           AND (lease_until_utc IS NULL OR lease_until_utc < {3})))
                ORDER BY created_at_utc
                LIMIT {4}
            )
            """,
            new object?[] { _ownerId, leaseUntil, workspaceId, Format(now), Math.Clamp(batch, 1, 500) },
            ct);
        var claimed = await db.TaskSchedulerIntents
            .AsNoTracking()
            .Where(item => item.Status == TaskSchedulerIntentStatuses.Processing
                           && item.LeaseOwner == _ownerId
                           && item.LeaseUntilUtc == leaseUntil)
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync(ct);
        await tx.CommitAsync(ct);
        if (claimed.Count > 0)
        {
            logger.LogDebug(
                "[TaskSchedulerIntent] claimed {Count} intents workspace={WorkspaceId} owner={Owner}",
                claimed.Count,
                workspaceId,
                _ownerId);
        }

        return claimed.Select(ToIntent).ToList();
    }

    public async Task CompleteAsync(string intentId, DateTimeOffset processedAtUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE task_scheduler_intents
            SET status = 'done', processed_at_utc = {0},
                lease_owner = NULL, lease_until_utc = NULL
            WHERE intent_id = {1} AND status = 'processing'
            """,
            new object?[] { Format(processedAtUtc), intentId },
            ct);
    }

    public async Task<bool> FailAsync(
        string intentId,
        string error,
        int maxAttempts,
        DateTimeOffset processedAtUtc,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cappedMax = Math.Clamp(maxAttempts, 1, 100);
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE task_scheduler_intents
            SET status = CASE WHEN attempt_count >= {0} THEN 'dead' ELSE 'pending' END,
                processed_at_utc = CASE WHEN attempt_count >= {0} THEN {1} ELSE NULL END,
                last_error = {2},
                lease_owner = NULL, lease_until_utc = NULL
            WHERE intent_id = {3} AND status = 'processing'
            """,
            new object?[] { cappedMax, Format(processedAtUtc), Truncate(error), intentId },
            ct);
        if (affected == 0)
        {
            return false;
        }

        var dead = await db.TaskSchedulerIntents
            .AsNoTracking()
            .Where(item => item.IntentId == intentId)
            .Select(item => item.Status)
            .SingleAsync(ct);
        return dead == TaskSchedulerIntentStatuses.Dead;
    }

    public async Task<long> GetTailCursorAsync(string source, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TaskSchedulerIntents
            .AsNoTracking()
            .Where(item => item.Source == source)
            .MaxAsync(item => (long?)item.SourceEventId, ct) ?? 0;
    }

    private static TaskSchedulerIntent ToIntent(TaskSchedulerIntentEntity entity) => new()
    {
        IntentId = entity.IntentId,
        WorkspaceId = entity.WorkspaceId,
        Source = entity.Source,
        SourceEventId = entity.SourceEventId,
        EventType = entity.EventType,
        TaskId = entity.TaskId,
        GoalRunId = entity.GoalRunId,
        PayloadJson = entity.PayloadJson,
        AttemptCount = entity.AttemptCount,
    };

    private static string Format(DateTimeOffset value) => value
        .ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Truncate(string? error)
    {
        var text = string.IsNullOrWhiteSpace(error) ? "unknown" : error;
        return text.Length <= 512 ? text : text[..512];
    }
}
