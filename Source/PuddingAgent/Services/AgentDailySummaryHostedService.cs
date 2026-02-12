using PuddingPlatform.Services;

namespace PuddingAgent.Services;

/// <summary>
/// Agent 私有消息日志每日摘要任务：每天本地 0 点汇总上一天普通日志。
/// </summary>
public sealed class AgentDailySummaryHostedService(
    AgentDailySummaryBatchService batchService,
    ILogger<AgentDailySummaryHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[AgentDailySummary] Scheduler started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var delay = DelayUntilNextLocalMidnight(now);
                logger.LogInformation(
                    "[AgentDailySummary] Next run in {Delay}; now={Now:O}",
                    delay,
                    now);

                await Task.Delay(delay, stoppingToken);
                now = DateTimeOffset.Now;
                var results = await batchService.GeneratePreviousDayAsync(now, stoppingToken);

                logger.LogInformation(
                    "[AgentDailySummary] Daily run completed day={Day} count={Count}",
                    now.AddDays(-1).ToString("yyyy-MM-dd"),
                    results.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[AgentDailySummary] Daily run failed; scheduler will continue.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        logger.LogInformation("[AgentDailySummary] Scheduler stopped.");
    }

    internal static TimeSpan DelayUntilNextLocalMidnight(DateTimeOffset now)
    {
        var todayMidnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var next = now == todayMidnight ? todayMidnight : todayMidnight.AddDays(1);
        return next - now;
    }
}
