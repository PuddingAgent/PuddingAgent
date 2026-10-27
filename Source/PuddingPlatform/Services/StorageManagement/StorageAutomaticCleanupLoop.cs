using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// 自动清理的**周期驱动**：按间隔跑一次试运行，直到取消。
///
/// 职责边界（2026-10-27，S1 第四部分）：
/// 协调器只管作业队列（单 writer、人工优先），节奏由本类型负责。分开的理由是可测——
/// 循环里最容易出错的地方恰恰是「等待 + 唤醒」这段粘合（历史上 CS0023 就出在这里：
/// Task.WhenAny 混入非泛型 Task 后 await 出 void，<c>!await</c> 不合法）。
/// 与队列解耦后，这个循环可以用毫秒级间隔被单测直接驱动。
/// </summary>
public sealed class StorageAutomaticCleanupLoop
{
    /// <summary>非正间隔会被夹到这个下限，避免配置写错导致热自旋。</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly StorageAutomaticCleanupDryRun _dryRun;
    private readonly ILogger<StorageAutomaticCleanupLoop> _logger;

    public StorageAutomaticCleanupLoop(
        StorageAutomaticCleanupDryRun dryRun,
        StorageAutomaticCleanupOptions options,
        ILogger<StorageAutomaticCleanupLoop> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _dryRun = dryRun;
        _logger = logger;

        var requested = options.EvaluationInterval;
        if (requested <= TimeSpan.Zero)
        {
            logger.LogWarning(
                "[StorageMaintenance:Auto] EvaluationInterval={Requested} is non-positive; clamped to {Clamped}",
                requested,
                MinimumInterval);
            requested = MinimumInterval;
        }

        Interval = requested;
    }

    /// <summary>实际生效的评估间隔（已夹紧）。供调用方核对配置是否真的生效。</summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// 运行周期评估，直到 <paramref name="cancellationToken"/> 取消。
    /// **永不抛出**：单轮失败只记日志，循环继续——否则一次探测异常就会让自动清理永久停摆。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await _dryRun.ExecuteAsync(DateTimeOffset.UtcNow, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StorageMaintenance:Auto] loop iteration crashed; continuing");
            }
        }
    }
}
