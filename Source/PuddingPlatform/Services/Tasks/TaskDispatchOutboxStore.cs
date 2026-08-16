using Microsoft.EntityFrameworkCore;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Tasks;

/// <summary>task_dispatch_outbox.status 的稳定 wire 值。</summary>
public static class TaskDispatchOutboxStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Dead = "dead";
}

/// <summary>Dispatcher 领取/读取用的 outbox 投影（含反序列化后的 Envelope）。</summary>
public sealed record TaskDispatchOutboxItem
{
    public required long Id { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string WorkspaceId { get; init; }
    public required string TaskId { get; init; }
    public required string AssignmentId { get; init; }
    public required string AgentId { get; init; }
    public required string Origin { get; init; }
    public required TaskInstructionEnvelope Envelope { get; init; }
    public required string Status { get; init; }
    public required int AttemptCount { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LeaseUntilUtc { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? SentAtUtc { get; init; }
}

/// <summary>
/// TB-05: Dispatch Outbox 持久化（领取/标记/绑定/恢复/幂等找回 Delivery）。
/// <para>
/// 写入侧（AppendDispatchOutbox）由 <see cref="TaskCommandService"/> 在 Assign/RunNow 的
/// 同一 EF SaveChanges 内完成（不变量 #6）；本 Store 负责 Dispatcher 侧的领取与终态推进，
/// 外部消息发送（<see cref="PuddingCode.Abstractions.IMessageSystem.SendAsync"/>）绝不发生
/// 在数据库事务内（不变量 #7）。
/// </para>
/// </summary>
public sealed class TaskDispatchOutboxStore(
    IDbContextFactory<PlatformDbContext> dbFactory)
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory = dbFactory;

    /// <summary>返回可领取的 outbox（pending/failed 且 lease 为空或已过期），按 id 升序。</summary>
    public async Task<IReadOnlyList<TaskDispatchOutboxItem>> PeekPendingOutboxAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.TaskDispatchOutbox
            .AsNoTracking()
            .Where(o => o.Status == TaskDispatchOutboxStatuses.Pending || o.Status == TaskDispatchOutboxStatuses.Failed)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        // lease 过期判定在内存做（避免 SQLite DateTimeOffset 比较翻译差异；pending 集合量小）。
        return entities
            .Where(o => o.LeaseUntilUtc == null || o.LeaseUntilUtc < now)
            .Select(ToItem)
            .ToList();
    }

    /// <summary>
    /// 原子领取：仅当状态为 pending/failed 且 lease 为空/已过期时成功，同时递增 attempt_count。
    /// 返回领取后的投影；领取失败（已被他人领取）返回 null。
    /// </summary>
    public async Task<TaskDispatchOutboxItem?> ClaimOutboxAsync(
        long outboxId,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entity = await db.TaskDispatchOutbox.SingleOrDefaultAsync(o => o.Id == outboxId, ct);
        if (entity is null
            || (entity.Status != TaskDispatchOutboxStatuses.Pending && entity.Status != TaskDispatchOutboxStatuses.Failed)
            || (entity.LeaseUntilUtc != null && entity.LeaseUntilUtc >= now))
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        entity.LeaseUntilUtc = leaseUntil;
        entity.AttemptCount += 1;
        entity.LastError = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToItem(entity);
    }

    /// <summary>
    /// 发送成功后原子推进（不变量 #6）：标记 outbox sent + 写 binding + Task Reserved→Assigned
    /// + 追加 task.assigned 事件。全部在一个 SaveChanges 提交，避免「已绑定但 Task 仍 Reserved」
    /// 的不可恢复中间态。
    /// </summary>
    public async Task CompleteDispatchAsync(
        long outboxId,
        string deliveryId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var outbox = await db.TaskDispatchOutbox.SingleOrDefaultAsync(o => o.Id == outboxId, ct);
        if (outbox is null)
        {
            return;
        }

        // 已 sent/dead 视为幂等完成（防御重复 CompleteDispatch 调用）。
        if (outbox.Status is TaskDispatchOutboxStatuses.Sent or TaskDispatchOutboxStatuses.Dead)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // ── 状态机 + stale assignment 守卫（不变量 #9：过期/非当前 Assignment 不能推进终态）──
        var task = await db.WorkspaceTasks
            .SingleOrDefaultAsync(t => t.TaskId == outbox.TaskId && t.WorkspaceId == outbox.WorkspaceId, ct);
        if (task is null)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{outbox.TaskId}' not found while completing dispatch.",
                outbox.TaskId);
        }

        if (task.Status != WorkspaceTaskStatus.Reserved || task.ActiveAssignmentId != outbox.AssignmentId)
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskStateConflict,
                $"Task '{outbox.TaskId}' is not Reserved for assignment '{outbox.AssignmentId}' (stale assignment).",
                outbox.TaskId,
                actualVersion: task.Version);
        }

        if (!TaskStateMachine.CanTransition(WorkspaceTaskStatus.Reserved, WorkspaceTaskStatus.Assigned))
        {
            throw new TaskStoreException(
                TaskErrorCode.TaskInvalidTransition,
                $"Task '{outbox.TaskId}' cannot transition Reserved→Assigned.",
                outbox.TaskId,
                actualVersion: task.Version);
        }

        task.Status = WorkspaceTaskStatus.Assigned;
        task.Version += 1;
        task.UpdatedAtUtc = now;

        // ── Assignment Attempt：Reserved → Assigned + ActiveAtUtc ──
        var attempt = await db.TaskAssignmentAttempts
            .SingleOrDefaultAsync(a => a.AttemptId == outbox.AssignmentId, ct);
        if (attempt is not null && attempt.Status == AssignmentAttemptStatus.Reserved)
        {
            attempt.Status = AssignmentAttemptStatus.Assigned;
            attempt.ActiveAtUtc = now;
            attempt.UpdatedAtUtc = now;
        }

        // ── Outbox → sent ──
        outbox.Status = TaskDispatchOutboxStatuses.Sent;
        outbox.SentAtUtc = now;
        outbox.LeaseUntilUtc = null;
        outbox.LastError = null;

        // ── Binding（幂等：同一 delivery 只写一次）──
        var bindingExists = await db.TaskExecutionBindings
            .AnyAsync(b => b.DeliveryId == deliveryId, ct);
        if (!bindingExists)
        {
            db.TaskExecutionBindings.Add(new TaskExecutionBindingEntity
            {
                TaskId = outbox.TaskId,
                AssignmentId = outbox.AssignmentId,
                DeliveryId = deliveryId,
                BoundAtUtc = now,
            });
        }

        // ── task.assigned 事件 ──
        var nextSequence = await db.TaskEvents
            .Where(e => e.TaskId == outbox.TaskId)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;
        db.TaskEvents.Add(new TaskEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"),
            TaskId = outbox.TaskId,
            WorkspaceId = outbox.WorkspaceId,
            Sequence = nextSequence + 1,
            EventType = TaskEventType.TaskAssigned,
            AssignmentId = outbox.AssignmentId,
            AgentId = outbox.AgentId,
            DeliveryId = deliveryId,
            CreatedAtUtc = now,
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>发送失败（可重试）：状态 → failed，清 lease，记录 error。</summary>
    public async Task MarkOutboxFailedAsync(long outboxId, string error, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var outbox = await db.TaskDispatchOutbox.SingleOrDefaultAsync(o => o.Id == outboxId, ct);
        if (outbox is null || outbox.Status is TaskDispatchOutboxStatuses.Sent or TaskDispatchOutboxStatuses.Dead)
        {
            return;
        }

        outbox.Status = TaskDispatchOutboxStatuses.Failed;
        outbox.LastError = error;
        outbox.LeaseUntilUtc = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>死信（重试耗尽）：状态 → dead，清 lease，记录 error。</summary>
    public async Task MarkOutboxDeadAsync(long outboxId, string error, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var outbox = await db.TaskDispatchOutbox.SingleOrDefaultAsync(o => o.Id == outboxId, ct);
        if (outbox is null || outbox.Status is TaskDispatchOutboxStatuses.Sent or TaskDispatchOutboxStatuses.Dead)
        {
            return;
        }

        outbox.Status = TaskDispatchOutboxStatuses.Dead;
        outbox.LastError = error;
        outbox.LeaseUntilUtc = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 恢复扫描（不变量 #9）：清掉 pending/failed 且 lease 已过期的租约，使其可被重新领取。
    /// 返回恢复条数。
    /// </summary>
    public async Task<int> RecoverPendingOutboxAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.TaskDispatchOutbox
            .Where(o => o.Status == TaskDispatchOutboxStatuses.Pending || o.Status == TaskDispatchOutboxStatuses.Failed)
            .ToListAsync(ct);

        var expired = candidates
            .Where(o => o.LeaseUntilUtc != null && o.LeaseUntilUtc < now)
            .ToList();
        foreach (var o in expired)
        {
            o.LeaseUntilUtc = null;
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return expired.Count;
    }

    /// <summary>按 delivery_id 找回 Binding（不变量 #8 / §5.3）。</summary>
    public async Task<TaskExecutionBindingEntity?> GetBindingByDeliveryIdAsync(
        string deliveryId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TaskExecutionBindings
            .AsNoTracking()
            .SingleOrDefaultAsync(b => b.DeliveryId == deliveryId, ct);
    }

    /// <summary>发送后去重（SendAsync 返回空 DeliveryIds）时，按 message_id 找回已持久 Delivery。</summary>
    public async Task<string?> FindDeliveryIdByMessageIdAsync(string messageId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.MessageDeliveries
            .AsNoTracking()
            .Where(d => d.MessageId == messageId)
            .Select(d => d.DeliveryId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>按 id 读取 outbox（测试/诊断用）。</summary>
    public async Task<TaskDispatchOutboxEntity?> GetOutboxAsync(long outboxId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TaskDispatchOutbox.AsNoTracking().SingleOrDefaultAsync(o => o.Id == outboxId, ct);
    }

    private static TaskDispatchOutboxItem ToItem(TaskDispatchOutboxEntity entity) => new()
    {
        Id = entity.Id,
        IdempotencyKey = entity.IdempotencyKey,
        WorkspaceId = entity.WorkspaceId,
        TaskId = entity.TaskId,
        AssignmentId = entity.AssignmentId,
        AgentId = entity.AgentId,
        Origin = entity.Origin,
        Envelope = TaskDispatchSerialization.Deserialize(entity.EnvelopePayload),
        Status = entity.Status,
        AttemptCount = entity.AttemptCount,
        LastError = entity.LastError,
        LeaseUntilUtc = entity.LeaseUntilUtc,
        CreatedAtUtc = entity.CreatedAtUtc,
        SentAtUtc = entity.SentAtUtc,
    };
}
