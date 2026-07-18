using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Durable projection scheduler for the Conversation Event Store.
/// It discovers work from persisted heads/checkpoints, so projection does not
/// depend on which event writer committed a batch or on an in-memory signal.
/// </summary>
public sealed class ConversationProjectionWorker(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ConversationProjector projector,
    ILogger<ConversationProjectionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    private const int ScanBatchSize = 32;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[ConversationProjectionWorker] Started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var conversationIds = await FindPendingConversationsAsync(stoppingToken);
                if (conversationIds.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                foreach (var conversationId in conversationIds)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var result = await projector.ProjectAsync(conversationId, stoppingToken);
                    if (result.Error is not null)
                    {
                        logger.LogWarning(
                            "[ConversationProjectionWorker] Projection deferred conv={ConversationId} error={Error}",
                            conversationId,
                            result.Error);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ConversationProjectionWorker] Scan failed");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }

        logger.LogInformation("[ConversationProjectionWorker] Stopped");
    }

    private async Task<IReadOnlyList<string>> FindPendingConversationsAsync(
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ConversationHeads
            .AsNoTracking()
            .GroupJoin(
                db.ConversationProjectionCheckpoints.AsNoTracking(),
                head => head.ConversationId,
                checkpoint => checkpoint.ConversationId,
                (head, checkpoints) => new
                {
                    head.ConversationId,
                    head.HeadSequence,
                    ProjectedThrough = checkpoints
                        .Select(checkpoint => (long?)checkpoint.ProjectedThrough)
                        .FirstOrDefault() ?? 0,
                })
            .Where(item => item.HeadSequence > item.ProjectedThrough)
            .OrderBy(item => item.ProjectedThrough)
            .ThenBy(item => item.ConversationId)
            .Select(item => item.ConversationId)
            .Take(ScanBatchSize)
            .ToListAsync(ct);
    }
}
