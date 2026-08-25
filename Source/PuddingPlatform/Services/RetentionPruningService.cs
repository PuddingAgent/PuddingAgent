using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Storage;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatform.Services;

/// <summary>
/// platform.db 自动保留期调度器（ADR-076 §5.2）。
///
/// 本服务自 ADR-076 实施起只负责"何时清理"：到期判断、抖动与策略读取；
/// 所有数据库删除统一委托 StorageMaintenanceCoordinator（唯一在线维护 writer），
/// 不再自行开启写连接，也不存在第二个清理 BackgroundService。
///
/// 策略来源：&lt;DataRoot&gt;/config/system.json → storageManagement.automaticCleanup。
/// 首期未实现长期聚合，因此 diagnostics.telemetry-raw / diagnostics.context-layer-raw
/// 的自动清理默认保持关闭（目录 RequiresRollupBeforeAutomatic），人工清理不受影响。
/// conversation_events 是 Evidence 路径：协调器执行 ArchiveAndDeleteRows（先归档后删）。
/// </summary>
public sealed class RetentionPruningService : BackgroundService
{
    private readonly StorageRetentionPolicyService _policyService;
    private readonly StorageMaintenanceCoordinator _coordinator;
    private readonly ILogger<RetentionPruningService> _logger;

    public RetentionPruningService(
        StorageRetentionPolicyService policyService,
        StorageMaintenanceCoordinator coordinator,
        ILogger<RetentionPruningService> logger)
    {
        _policyService = policyService;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync 会同步执行到第一个未完成 await；
        // 先 Yield 保证宿主启动与 Desktop Ready 信号永不被保留期扫描阻塞。
        await Task.Yield();

        try
        {
            await RunLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[RetentionPruning] cancelled by host shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RetentionPruning] scheduler failed");
        }
    }

    /// <summary>主循环：启动延迟后按策略间隔到期清理。public 便于测试直调。</summary>
    public async Task RunLoopAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var policy = await _policyService.GetEffectivePolicyAsync(ct);
            if (!policy.AutomaticCleanupEnabled)
            {
                _logger.LogInformation("[RetentionPruning] automatic cleanup disabled by policy");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, policy.StartupDelaySeconds)), ct);

            var interval = TimeSpan.FromHours(Math.Max(1, policy.RunIntervalHours));
            // 每小时复查一次到期状态（策略可在运行中被修改），到期即执行并重置等待。
            while (!ct.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var dueAt = policy.LastCompletedAtUtc?.Add(interval)
                            // 首次运行加小幅抖动，避免多个环境同一时间开始。
                            ?? now.AddMinutes(Random.Shared.Next(1, 10));

                if (now >= dueAt)
                {
                    try
                    {
                        await RunOnceAsync(ct);
                        await _policyService.MarkAutomaticRunCompletedAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RetentionPruning] sweep failed; will retry next interval");
                    }

                    _policyService.InvalidateCache();
                    policy = await _policyService.GetEffectivePolicyAsync(ct);
                    if (!policy.AutomaticCleanupEnabled)
                        return;
                    continue;
                }

                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
        }
    }

    /// <summary>单轮到期清理：逐目标提交协调器作业并等待终态。public 便于测试直调。</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var policy = await _policyService.GetEffectivePolicyAsync(ct);
        if (!policy.AutomaticCleanupEnabled)
        {
            _logger.LogInformation("[RetentionPruning] disabled by policy, skip sweep");
            return;
        }

        if (policy.Warnings.Count > 0)
            foreach (var warning in policy.Warnings)
                _logger.LogWarning("[RetentionPruning] policy warning: {Warning}", warning);

        var now = DateTimeOffset.UtcNow;
        foreach (var target in policy.Targets)
        {
            ct.ThrowIfCancellationRequested();
            if (!target.Enabled || target.Suspended)
                continue;

            var cutoff = now.AddDays(-target.RetentionDays);
            var job = await _coordinator.SubmitAutomaticAsync([target.TargetId], cutoff, ct);
            _logger.LogInformation(
                "[RetentionPruning] submitted target={Target} cutoff={Cutoff:O} job={JobId}",
                target.TargetId, cutoff, job.JobId);

            // 等待终态：自动作业每轮 200 批后让位人工作业，循环直到完成。
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                job = await _coordinator.WaitForNextCheckpointAsync(job.JobId, TimeSpan.FromMinutes(5), ct);
                if (job.Status is StorageCleanupJobStatus.Completed
                    or StorageCleanupJobStatus.Partial
                    or StorageCleanupJobStatus.Failed
                    or StorageCleanupJobStatus.Cancelled
                    or StorageCleanupJobStatus.NeedsConfirmation)
                {
                    break;
                }
            }

            _logger.LogInformation(
                "[RetentionPruning] target={Target} status={Status} deleted={Deleted} cleared={Cleared} warnings={Warnings}",
                target.TargetId, job.Status, job.DeletedRows, job.ClearedRows, job.Warnings.Count);
        }
    }
}
