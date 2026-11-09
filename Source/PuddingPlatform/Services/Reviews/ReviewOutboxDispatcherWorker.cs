using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Services.Reviews;

/// <summary>
/// PA-03 S5 / 07A dispatch loop: periodically drains the review-completed outbox through the product sink. The
/// loop is low frequency with bounded exponential backoff on failures; correctness does not depend on timing —
/// the durable outbox row stays unpublished until the sink accepts it, so a restart or missed tick only delays
/// delivery.
/// </summary>
public sealed class ReviewOutboxDispatcherWorker(ReviewEventDispatcher dispatcher, ILogger? logger = null,
    TimeSpan? interval = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(1);

    public async Task<ReviewDispatchResult> DispatchOnceAsync(CancellationToken ct = default)
        => await dispatcher.DispatchBatchAsync(20, ct).ConfigureAwait(false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = _interval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
                delay = result.Failed > 0
                    ? Min(delay * 2, _maxBackoff)
                    : _interval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "PA-03 review outbox dispatch pass failed.");
                delay = Min(delay * 2, _maxBackoff);
            }
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
