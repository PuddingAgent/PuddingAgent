using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingPlatform.Services;

/// <summary>
/// 将已持久化但尚未进入 canonical Conversation Event Store 的子代理事件补写。
/// 即时投影失败或进程在提交后退出时，下一轮扫描从每个 Run 的持久游标继续。
/// </summary>
public sealed class SubAgentConversationProjectionWorker(
    ISubAgentRunStore runStore,
    ISessionStateManager sessionState,
    ILogger<SubAgentConversationProjectionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly DateTimeOffset _processStartedAtUtc = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var recovered = await runStore.RecoverInterruptedRunsAsync(
                _processStartedAtUtc,
                maxRuns: 10_000,
                stoppingToken);
            if (recovered > 0)
            {
                logger.LogWarning(
                    "[SubAgentProjection] Recovered {Count} non-terminal runs from a previous process as interrupted",
                    recovered);
            }

            // session_sub_agents 是另一张投影表，此前没有任何对账入口：
            // 2026-11-20 实测积了 10 条自 2026-07-23 起的 running，占着「运行中」计数。
            var recoveredProjections = await sessionState.RecoverInterruptedSubAgentsAsync(
                _processStartedAtUtc,
                maxRows: 10_000,
                stoppingToken);
            if (recoveredProjections > 0)
            {
                logger.LogWarning(
                    "[SubAgentProjection] Recovered {Count} non-terminal sub-agent projections from a previous process as failed",
                    recoveredProjections);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SubAgentProjection] Interrupted-run recovery failed");
        }

        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await runStore.ReplayPendingConversationEventsAsync(
                    maxRuns: 100,
                    stoppingToken);
                if (count > 0)
                {
                    logger.LogDebug(
                        "[SubAgentProjection] Projected {Count} persisted events",
                        count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SubAgentProjection] Projection scan failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
