using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;

namespace PuddingPlatform.Services.Tasks;

/// <summary>TaskDispatcher 配置。</summary>
public sealed class TaskDispatcherOptions
{
    /// <summary>扫描间隔（默认 5s）。</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>领取租约时长（默认 2min）。</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>发送失败重试上限（超过 → dead）。</summary>
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// TB-05: 手工派发 Dispatcher（Hosted Service + 恢复扫描 + 幂等绑定）。
/// <para>
/// 流程（ADR-072 §8.1）：pending Outbox → Fence(dispatch) stub → Message Fabric SendAsync
/// （幂等，通过稳定 MessageId 去重）→ bind Delivery → Task Reserved→Assigned。外部发送绝不
/// 在 DB 事务内（不变量 #7）；「发送成功但未绑定」崩溃后按 idempotency key 找回同一 Delivery
/// （不变量 #8）；过期租约不重复发送（不变量 #9）。
/// </para>
/// </summary>
public sealed class TaskDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkAdmissionFence _fence;
    private readonly TimeProvider _timeProvider;
    private readonly TaskDispatcherOptions _options;
    private readonly ILogger<TaskDispatcher> _logger;

    public TaskDispatcher(
        IServiceScopeFactory scopeFactory,
        IWorkAdmissionFence fence,
        TimeProvider timeProvider,
        IOptions<TaskDispatcherOptions> options,
        ILogger<TaskDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _fence = fence;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TaskDispatcher] dispatch scan failed.");
            }

            try
            {
                await Task.Delay(_options.ScanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>单轮扫描：恢复过期租约 → 领取并派发 pending outbox。返回处理的条数。</summary>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<TaskDispatchOutboxStore>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
        var taskStore = scope.ServiceProvider.GetRequiredService<ITaskStore>();

        var now = _timeProvider.GetUtcNow();
        await store.RecoverPendingOutboxAsync(now, ct);

        var pending = await store.PeekPendingOutboxAsync(now, ct);
        var processed = 0;
        foreach (var entry in pending)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var claimUntil = _timeProvider.GetUtcNow().Add(_options.LeaseDuration);
            var claimed = await store.ClaimOutboxAsync(entry.Id, now, claimUntil, ct);
            if (claimed is null)
            {
                continue;
            }

            processed++;
            try
            {
                await DispatchOneAsync(scope, store, messages, taskStore, claimed, ct);
            }
            catch (Exception ex)
            {
                // 单条失败不中断整轮扫描；失败按 attempt_count 上限在 DispatchOneAsync 内已标记。
                _logger.LogError(
                    ex,
                    "[TaskDispatcher] dispatch failed outbox_id={OutboxId} task_id={TaskId}",
                    claimed.Id,
                    claimed.TaskId);
            }
        }

        return processed;
    }

    private async Task DispatchOneAsync(
        IServiceScope scope,
        TaskDispatchOutboxStore store,
        IMessageSystem messages,
        ITaskStore taskStore,
        TaskDispatchOutboxItem entry,
        CancellationToken ct)
    {
        // ── Fence(dispatch)（本阶段 ManualAlwaysAllowFence 恒 allow）──
        var task = await taskStore.GetTaskAsync(entry.WorkspaceId, entry.TaskId, ct)
            ?? throw new TaskStoreException(
                TaskErrorCode.TaskNotFound,
                $"Task '{entry.TaskId}' not found while dispatching.",
                entry.TaskId);
        var decision = await _fence.EvaluateAsync(new WorkAdmissionFenceInput
        {
            WorkspaceId = entry.WorkspaceId,
            TaskId = entry.TaskId,
            AssignmentId = entry.AssignmentId,
            AgentId = entry.AgentId,
            TaskStatus = task.Status,
            Priority = task.Priority,
            ExecutionWindow = task.ExecutionWindow,
            EvaluatedAtUtc = _timeProvider.GetUtcNow(),
        }, ct);

        if (decision.Verdict != FenceVerdict.Allow)
        {
            // 完整 Fence 语义（defer 保留、deny 终态）留待 AU-01；stub 阶段非 allow 一律按可重试失败记录。
            await store.MarkOutboxFailedAsync(
                entry.Id,
                $"fence:{decision.Verdict.ToString().ToLowerInvariant()}:{decision.Code}",
                ct);
            return;
        }

        // ── 快照 expected_version（派发时刻 Task 即将被 CompleteDispatchAsync 推进 Reserved→Assigned，Assigned 版本 = Reserved + 1）──
        var envelope = entry.Envelope with { ExpectedVersion = task.Version + 1 };

        // ── Message Fabric SendAsync（幂等，外部发送不在 DB 事务内，不变量 #7）──
        MessageSendResult result;
        try
        {
            result = await messages.SendAsync(envelope.ToMessageEnvelope(), ct);
        }
        catch (Exception ex)
        {
            if (entry.AttemptCount >= _options.MaxAttempts)
            {
                await store.MarkOutboxDeadAsync(entry.Id, ex.Message, ct);
            }
            else
            {
                await store.MarkOutboxFailedAsync(entry.Id, ex.Message, ct);
            }

            return;
        }

        // ── 幂等找回 Delivery（不变量 #8）：首次发送返回 deliveryId；去重后按 message_id 找回 ──
        string deliveryId;
        if (result.DeliveryIds.Count > 0)
        {
            deliveryId = result.DeliveryIds[0];
        }
        else
        {
            deliveryId = await store.FindDeliveryIdByMessageIdAsync(envelope.MessageId, ct)
                ?? throw new InvalidOperationException(
                    $"Delivery not found for message '{envelope.MessageId}' after idempotent re-send.");
        }

        // ── 原子推进：outbox sent + binding + Task Reserved→Assigned + task.assigned 事件 ──
        await store.CompleteDispatchAsync(entry.Id, deliveryId, ct);
    }
}
