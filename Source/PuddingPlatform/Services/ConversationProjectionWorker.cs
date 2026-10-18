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
    // When a streaming response is active, the head usually advances by only a few token events
    // between scans. Coalesce those fragments before the next durable projection pass; SSE remains
    // the live UI path, while the materialized transcript stays near-real-time without a tight
    // read/checkpoint/catalog write loop.
    private static readonly TimeSpan ActiveCoalescingDelay = TimeSpan.FromMilliseconds(750);
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

                // If any conversation still has more than one projector batch pending, immediately
                // catch up. Otherwise allow active streams to accumulate a useful batch.
                var hasBacklog = await HasProjectionBacklogAsync(stoppingToken);
                if (!hasBacklog)
                {
                    await Task.Delay(ActiveCoalescingDelay, stoppingToken);
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
        var headPending = await db.ConversationHeads
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

        // 旧 Core 只推进 checkpoint、不写读模型时，head == checkpoint ⇒ 上面的查询永远
        // 看不到该会话。这里额外发现 read_state 明显落后于 checkpoint 的会话，交给
        // Projector.ResolveResumeSequenceAsync 从读模型位置补放尾部事件。
        var stale = await FindStaleReadModelConversationsAsync(db, ct);
        if (stale.Count == 0)
            return headPending;

        return headPending
            .Concat(stale)
            .Distinct(StringComparer.Ordinal)
            .Take(ScanBatchSize)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> FindStaleReadModelConversationsAsync(
        PlatformDbContext db,
        CancellationToken ct)
    {
        var readStates = await db.ConversationReadStates
            .AsNoTracking()
            .Select(item => new { item.ConversationId, item.UpdatedAt })
            .ToListAsync(ct);
        if (readStates.Count == 0)
            return [];

        var conversationIds = readStates.Select(item => item.ConversationId).ToArray();
        var checkpoints = await db.ConversationProjectionCheckpoints
            .AsNoTracking()
            .Where(item => conversationIds.Contains(item.ConversationId))
            .Select(item => new { item.ConversationId, item.ProjectedThrough, item.UpdatedAt })
            .ToDictionaryAsync(item => item.ConversationId, StringComparer.Ordinal, ct);

        var stale = new List<string>();
        foreach (var readState in readStates)
        {
            if (!checkpoints.TryGetValue(readState.ConversationId, out var checkpoint)
                || checkpoint.ProjectedThrough <= 0
                || !DateTimeOffset.TryParse(readState.UpdatedAt, out var readStateUpdatedAt)
                || !DateTimeOffset.TryParse(checkpoint.UpdatedAt, out var checkpointUpdatedAt))
            {
                continue;
            }

            if (checkpointUpdatedAt - readStateUpdatedAt <= TimeSpan.FromMinutes(1))
                continue;

            var appliedThrough = await db.ConversationTurnSummaries
                .AsNoTracking()
                .Where(item => item.ConversationId == readState.ConversationId)
                .MaxAsync(item => (long?)item.LastSequence, ct) ?? 0L;
            if (appliedThrough > 0 && appliedThrough < checkpoint.ProjectedThrough)
                stale.Add(readState.ConversationId);
        }

        return stale;
    }

    private async Task<bool> HasProjectionBacklogAsync(CancellationToken ct)
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
                    head.HeadSequence,
                    ProjectedThrough = checkpoints
                        .Select(checkpoint => (long?)checkpoint.ProjectedThrough)
                        .FirstOrDefault() ?? 0,
                })
            .AnyAsync(item => item.HeadSequence - item.ProjectedThrough >= 200, ct);
    }
}
