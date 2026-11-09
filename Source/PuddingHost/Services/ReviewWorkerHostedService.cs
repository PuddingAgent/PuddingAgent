using Microsoft.Extensions.Hosting;
using PuddingPlatform.Services.Reviews;

namespace PuddingHost.Services;

/// <summary>
/// PA-03 review worker loop: bounded batch, short lease, correctness independent of tick timing (durable store/outbox).
/// A failed batch is logged and retried on the next tick; cancellation stops immediately.
/// </summary>
public sealed class ReviewWorkerHostedService(ReviewJobWorker worker, ILogger<ReviewWorkerHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
                var result = await worker.RunBatchAsync(BatchSize, Lease, stoppingToken).ConfigureAwait(false);
                if (result.Executed > 0 || result.Failed > 0)
                    logger.LogInformation("PA-03 review batch claimed={Claimed} executed={Executed} skipped={Skipped} failed={Failed}",
                        result.Claimed, result.Executed, result.Skipped, result.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PA-03 review worker batch failed; the durable job/outbox state is retried on the next tick.");
            }
        }
    }
}
