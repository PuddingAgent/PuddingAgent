using PuddingPlatform.Services;

namespace PuddingAgent.Services;

/// <summary>
/// 启动时补偿摘要服务：Pudding 启动后立即对昨天进行摘要补偿，弥补 0:00 定时任务因停机导致的遗漏。
/// fire-and-forget 执行，不阻塞启动流程。
/// </summary>
public sealed class StartupDailySummaryCompensationService(
    AgentDailySummaryBatchService batchService,
    ILogger<StartupDailySummaryCompensationService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var yesterday = DateTimeOffset.Now.AddDays(-1).ToString("yyyy-MM-dd");
                logger.LogInformation(
                    "[StartupDailySummaryCompensation] Starting compensation for day={Day}",
                    yesterday);

                var results = await batchService.GenerateForDayAsync(yesterday, ct);

                var generated = results.Count(r => !r.Skipped);
                var skipped = results.Count(r => r.Skipped);
                logger.LogInformation(
                    "[StartupDailySummaryCompensation] Compensation completed day={Day} generated={Generated} skipped={Skipped}",
                    yesterday,
                    generated,
                    skipped);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[StartupDailySummaryCompensation] Compensation failed; Pudding continues normally.");
            }
        }, ct);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
