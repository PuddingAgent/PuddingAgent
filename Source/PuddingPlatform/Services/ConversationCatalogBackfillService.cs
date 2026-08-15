using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// P0-4f 第⑤步第⑤小步 C3：conversation_catalog 历史回填（幂等 backfill）。
/// <para>
/// C2 只覆盖增量（ConversationProjector 按 checkpoint 投影），C2 上线前已推进 checkpoint 的
/// 存量 conversation 其 catalog 行缺失。本服务遍历全部 conversation，从 sequence=0 重放事件，
/// 逐事件调用 <see cref="ConversationCatalogWriter"/> 的权威 UPSERT，补齐存量 catalog 行。
/// </para>
/// <para><b>幂等</b>：UPSERT 采用 ON CONFLICT(conversation_id) DO UPDATE；重跑结果收敛，
/// 不产生重复行。created_at 仅 INSERT 写一次、title/agent_id/principal_id/successor 均以
/// COALESCE 保留既有非空值、status 以 CASE 保留既有值，因此重放顺序确定时最终行确定。</para>
/// </summary>
public sealed class ConversationCatalogBackfillService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IConversationEventStore eventStore,
    ConversationCatalogWriter catalogWriter,
    ILogger<ConversationCatalogBackfillService> logger)
{
    private const int BatchSize = 200;

    public sealed class BackfillResult
    {
        public int ConversationsScanned { get; set; }
        public int ConversationsWithEvents { get; set; }
        public long EventsProcessed { get; set; }
        public int Errors { get; set; }
        public List<string> ErrorDetails { get; set; } = [];
        public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 回填主流程（幂等，可重复调用）：
    /// a) 确保 conversation_events / conversation_catalog 等表存在；
    /// b) 从 conversation_events 列出全部 distinct conversation_id（裸 SQL，EventStore 无列 conversation 能力）；
    /// c) 对每个 conversation 从 sequence=0 起按 200 一页 ReadForwardAsync 重放全部事件，
    ///    逐事件调用 catalogWriter.UpsertCatalogRowAsync。
    /// </summary>
    public async Task<BackfillResult> BackfillAsync(CancellationToken ct = default)
    {
        var result = new BackfillResult();

        await eventStore.EnsureTablesAsync(ct);

        // b) 列全部 conversation。IConversationEventStore 没有列 conversation 能力，
        //    用 PlatformDbContext 直接查 conversation_events 的 distinct conversation_id。
        IReadOnlyList<string> conversationIds;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            conversationIds = await db.ConversationEvents
                .AsNoTracking()
                .Select(e => e.ConversationId)
                .Distinct()
                .ToListAsync(ct);
        }

        result.ConversationsScanned = conversationIds.Count;

        foreach (var conversationId in conversationIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var processedForConversation = await ReplayConversationAsync(conversationId, ct);
                if (processedForConversation > 0)
                {
                    result.ConversationsWithEvents++;
                }
                result.EventsProcessed += processedForConversation;
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.ErrorDetails.Add($"conv={conversationId}: {ex.Message}");
                logger.LogWarning(
                    ex,
                    "[ConversationCatalogBackfill] backfill failed conv={ConversationId}",
                    conversationId);
            }
        }

        logger.LogInformation(
            "[ConversationCatalogBackfill] Complete conversations={Conversations} withEvents={WithEvents} events={Events} errors={Errors}",
            result.ConversationsScanned,
            result.ConversationsWithEvents,
            result.EventsProcessed,
            result.Errors);

        return result;
    }

    /// <summary>
    /// 从 sequence=0 重放单个 conversation 的全部事件，逐事件 UPSERT catalog 行。
    /// </summary>
    private async Task<long> ReplayConversationAsync(string conversationId, CancellationToken ct)
    {
        long processed = 0;
        long afterExclusive = 0; // sequence 从 1 开始，sequence > 0 即全部事件

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await eventStore.ReadForwardAsync(
                conversationId, afterExclusive, null, BatchSize, ct);

            foreach (var evt in page.Events)
            {
                await catalogWriter.UpsertCatalogRowAsync(evt, ct);
                processed++;
            }

            if (!page.HasMore || page.Events.Count == 0)
            {
                break;
            }

            afterExclusive = page.Events[^1].Sequence;
        }

        return processed;
    }
}
