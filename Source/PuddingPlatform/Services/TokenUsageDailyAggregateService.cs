using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>已结束 UTC 日的一条 Token 用量聚合（day × source × provider × model）。</summary>
public sealed record LlmUsageDailyAggregateRow(
    string DayUtc,
    string Source,
    string ProviderId,
    string ModelId,
    long PromptTokens,
    long CompletionTokens,
    long CacheHitTokens,
    long CacheMissTokens,
    long RequestCount,
    decimal InputCost,
    decimal CacheHitCost,
    decimal OutputCost,
    decimal TotalCost);

/// <summary>
/// Token 统计按日聚合缓存（ADR-018/ADR-043 统计口径的渐进加载层）。
/// 已结束的 UTC 日在首次被查询时聚合一次并写入 llm_usage_daily_aggregates（含零数据日的完成标记），
/// 之后的请求直接读缓存；只有当前 UTC 日从 llm_gateway_usage_events / TokenUsageEvents 实时计算。
/// 账本重建（TokenUsageRebuildService）后按月失效，下次查询自动重算。
/// </summary>
public sealed class TokenUsageDailyAggregateService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<TokenUsageDailyAggregateService> logger)
{
    private const int BuildChunkDays = 31;
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    /// <summary>
    /// 返回 [startUtcDate, endUtcDateExclusive) 内已结束 UTC 日的聚合行，缺失日期按需构建。
    /// 范围末端自动裁剪到今天之前。
    /// </summary>
    public async Task<IReadOnlyList<LlmUsageDailyAggregateRow>> GetClosedDaysAsync(
        DateTime startUtcDate,
        DateTime endUtcDateExclusive,
        CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        if (endUtcDateExclusive > today)
            endUtcDateExclusive = today;
        if (startUtcDate >= endUtcDateExclusive)
            return [];

        await EnsureBuiltAsync(startUtcDate, endUtcDateExclusive, ct);

        var startDay = DailyCacheUtility.FormatDay(startUtcDate);
        var endDay = DailyCacheUtility.FormatDay(endUtcDateExclusive);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LlmUsageDailyAggregates.AsNoTracking()
            .Where(a => string.Compare(a.DayUtc, startDay) >= 0 && string.Compare(a.DayUtc, endDay) < 0)
            .Select(a => new LlmUsageDailyAggregateRow(
                a.DayUtc,
                a.Source,
                a.ProviderId,
                a.ModelId,
                a.PromptTokens,
                a.CompletionTokens,
                a.CacheHitTokens,
                a.CacheMissTokens,
                a.RequestCount,
                a.InputCost,
                a.CacheHitCost,
                a.OutputCost,
                a.TotalCost))
            .ToListAsync(ct);
    }

    /// <summary>当前 UTC 日的实时聚合（数据量小，不落缓存）。</summary>
    public async Task<IReadOnlyList<LlmUsageDailyAggregateRow>> GetLiveTodayAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await AggregateRangeAsync(db, today, today.AddDays(1), ct);
    }

    /// <summary>账本重建后失效缓存：yearMonth 为空时清空全部。</summary>
    public async Task InvalidateAsync(string? yearMonth, CancellationToken ct = default)
    {
        await _buildGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (string.IsNullOrWhiteSpace(yearMonth))
            {
                db.LlmUsageDailyAggregates.RemoveRange(db.LlmUsageDailyAggregates);
                db.StatsDailyCacheDays.RemoveRange(db.StatsDailyCacheDays
                    .Where(d => d.CacheKey == DailyCacheUtility.TokenUsageCacheKey));
            }
            else
            {
                db.LlmUsageDailyAggregates.RemoveRange(
                    db.LlmUsageDailyAggregates.Where(a => a.YearMonth == yearMonth));
                db.StatsDailyCacheDays.RemoveRange(db.StatsDailyCacheDays
                    .Where(d => d.CacheKey == DailyCacheUtility.TokenUsageCacheKey && d.DayUtc.StartsWith(yearMonth)));
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "[TokenDailyAggregate] Invalidated yearMonth={YearMonth}",
                string.IsNullOrWhiteSpace(yearMonth) ? "<all>" : yearMonth);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private async Task EnsureBuiltAsync(DateTime startUtcDate, DateTime endUtcDateExclusive, CancellationToken ct)
    {
        await _buildGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var builtDays = await DailyCacheUtility.LoadBuiltDaysAsync(
                db,
                DailyCacheUtility.TokenUsageCacheKey,
                startUtcDate,
                endUtcDateExclusive,
                ct);
            var missingRuns = DailyCacheUtility
                .EnumerateMissingRuns(startUtcDate, endUtcDateExclusive, builtDays)
                .ToList();
            if (missingRuns.Count == 0)
                return;

            var totalRows = 0;
            foreach (var (runStart, runEndExclusive) in missingRuns)
            {
                var chunkStart = runStart;
                while (chunkStart < runEndExclusive)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunkEnd = chunkStart.AddDays(BuildChunkDays) < runEndExclusive
                        ? chunkStart.AddDays(BuildChunkDays)
                        : runEndExclusive;

                    var rows = await AggregateRangeAsync(db, chunkStart, chunkEnd, ct);
                    await WriteChunkAsync(db, chunkStart, chunkEnd, rows, ct);
                    totalRows += rows.Count;
                    chunkStart = chunkEnd;
                }
            }

            logger.LogInformation(
                "[TokenDailyAggregate] Built closed-day cache {Start}..{EndExclusive} runs={Runs} rows={Rows}",
                DailyCacheUtility.FormatDay(startUtcDate),
                DailyCacheUtility.FormatDay(endUtcDateExclusive),
                missingRuns.Count,
                totalRows);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private static async Task WriteChunkAsync(
        PlatformDbContext db,
        DateTime chunkStart,
        DateTime chunkEndExclusive,
        IReadOnlyList<LlmUsageDailyAggregateRow> rows,
        CancellationToken ct)
    {
        var days = DailyCacheUtility.EnumerateDays(chunkStart, chunkEndExclusive)
            .Select(DailyCacheUtility.FormatDay)
            .ToList();
        var builtAt = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // 标记缺失意味着不应有存量行；按天防御性删除，保证重建幂等（唯一索引兜底）。
        db.LlmUsageDailyAggregates.RemoveRange(
            db.LlmUsageDailyAggregates.Where(a => days.Contains(a.DayUtc)));
        db.LlmUsageDailyAggregates.AddRange(rows.Select(r => new LlmUsageDailyAggregateEntity
        {
            DayUtc = r.DayUtc,
            YearMonth = r.DayUtc[..7],
            Source = r.Source,
            ProviderId = r.ProviderId,
            ModelId = r.ModelId,
            PromptTokens = r.PromptTokens,
            CompletionTokens = r.CompletionTokens,
            CacheHitTokens = r.CacheHitTokens,
            CacheMissTokens = r.CacheMissTokens,
            RequestCount = r.RequestCount,
            InputCost = r.InputCost,
            CacheHitCost = r.CacheHitCost,
            OutputCost = r.OutputCost,
            TotalCost = r.TotalCost,
            BuiltAtUtc = builtAt,
        }));
        db.StatsDailyCacheDays.AddRange(days.Select(day => new StatsDailyCacheDayEntity
        {
            CacheKey = DailyCacheUtility.TokenUsageCacheKey,
            DayUtc = day,
            BuiltAtUtc = builtAt,
        }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<List<LlmUsageDailyAggregateRow>> AggregateRangeAsync(
        PlatformDbContext db,
        DateTime startUtc,
        DateTime endUtcExclusive,
        CancellationToken ct)
    {
        var startText = DailyCacheUtility.ToSqliteUtcText(startUtc);
        var endText = DailyCacheUtility.ToSqliteUtcText(endUtcExclusive);

        var gatewayRaw = await db.LlmGatewayUsageEvents
            .FromSqlInterpolated($"""
                SELECT * FROM llm_gateway_usage_events
                WHERE occurred_at_utc >= {startText} AND occurred_at_utc < {endText}
                """)
            .AsNoTracking()
            .Select(e => new UsageRaw(
                e.OccurredAtUtc,
                e.ProviderId,
                e.ModelId,
                e.PromptTokens,
                e.CompletionTokens,
                e.CacheHitTokens,
                e.CacheMissTokens,
                e.InputCost,
                e.CacheHitCost,
                e.OutputCost,
                e.TotalCost))
            .ToListAsync(ct);

        var legacyRaw = await db.TokenUsageEvents
            .FromSqlInterpolated($"""
                SELECT * FROM "TokenUsageEvents"
                WHERE "OccurredAtUtc" >= {startText} AND "OccurredAtUtc" < {endText}
                """)
            .AsNoTracking()
            .Select(e => new UsageRaw(
                e.OccurredAtUtc,
                e.ProviderId ?? "unknown",
                e.ModelId ?? "unknown",
                e.PromptTokens,
                e.CompletionTokens,
                e.CacheHitTokens,
                e.CacheMissTokens,
                e.InputCost,
                e.CacheHitCost,
                e.OutputCost,
                e.TotalCost))
            .ToListAsync(ct);

        return GroupRows(gatewayRaw, LlmUsageAggregateSources.Gateway)
            .Concat(GroupRows(legacyRaw, LlmUsageAggregateSources.Legacy))
            .ToList();
    }

    private static IEnumerable<LlmUsageDailyAggregateRow> GroupRows(IEnumerable<UsageRaw> raw, string source)
        => raw
            .GroupBy(r => (Day: r.OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd"), r.ProviderId, r.ModelId))
            .Select(g => new LlmUsageDailyAggregateRow(
                g.Key.Day,
                source,
                g.Key.ProviderId,
                g.Key.ModelId,
                g.Sum(r => r.PromptTokens),
                g.Sum(r => r.CompletionTokens),
                g.Sum(r => r.CacheHitTokens),
                g.Sum(r => r.CacheMissTokens),
                g.LongCount(),
                g.Sum(r => r.InputCost),
                g.Sum(r => r.CacheHitCost),
                g.Sum(r => r.OutputCost),
                g.Sum(r => r.TotalCost)));

    private sealed record UsageRaw(
        DateTimeOffset OccurredAtUtc,
        string ProviderId,
        string ModelId,
        long PromptTokens,
        long CompletionTokens,
        long CacheHitTokens,
        long CacheMissTokens,
        decimal InputCost,
        decimal CacheHitCost,
        decimal OutputCost,
        decimal TotalCost);
}
