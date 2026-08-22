using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 单个 (day × layer × provider × model) 的可合并分析原料。
/// 标量全部可加；TokenCounts/HitRates 保留逐事件分布，Hashes 已按日去重，
/// 因此跨日合并后仍能精确还原 median / P95 / distinctHashes。
/// </summary>
public sealed record ContextLayerRollupPayload(
    long Calls,
    long TokenCount,
    long RawUtf8Bytes,
    long GzipBytes,
    long EstimatedHitTokens,
    long EstimatedMissTokens,
    double HitRateSum,
    long HitRateCount,
    long ChangeCount,
    long[] TokenCounts,
    double[] HitRates,
    string[] Hashes,
    Dictionary<string, long> ChangeReasons);

public sealed record ContextLayerRollupRow(
    string DayUtc,
    string LayerName,
    int LayerOrder,
    string LayerRole,
    string? ProviderId,
    string? ModelId,
    ContextLayerRollupPayload Payload);

public sealed record ContextLayerReasonCount(string Reason, long Count);

public sealed record ContextLayerAnalysis(
    long TotalEvents,
    long TotalLayerTokens,
    long TotalRawUtf8Bytes,
    long TotalGzipBytes,
    IReadOnlyList<ContextLayerAnalysisRow> Layers);

public sealed record ContextLayerAnalysisRow(
    string LayerName,
    int LayerOrder,
    string LayerRole,
    long Calls,
    long TokenCount,
    long RawUtf8Bytes,
    long GzipBytes,
    double GzipRatio,
    double TokenShare,
    double AvgTokens,
    double MedianTokens,
    double P95Tokens,
    long EstimatedHitTokens,
    long EstimatedMissTokens,
    double AvgCacheHitRate,
    double MedianCacheHitRate,
    long ChangeCount,
    double ChangeRate,
    int DistinctHashes,
    IReadOnlyList<ContextLayerReasonCount> ChangeReasons);

/// <summary>
/// 上下文层级分析的按日 rollup 缓存（ADR-018/ADR-043）。
/// 已结束 UTC 日把 context_layer_metric_events 聚合成 JSON 分布缓存；
/// 当前 UTC 日实时计算；查询边界不是整天时，边界日直接明细计算，保证任意
/// from/to 精度不变。sessionId 过滤不走缓存（无预聚合维度）。
/// </summary>
public sealed class ContextLayerDailyRollupService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<ContextLayerDailyRollupService> logger)
{
    private const int BuildChunkDays = 7;
    private static readonly JsonSerializerOptions PayloadJsonOpts = new()
    {
        // 数值数组多、哈希长，去掉缩进可显著减小 payload
        WriteIndented = false,
    };

    private readonly SemaphoreSlim _buildGate = new(1, 1);

    /// <summary>
    /// 按 from/to（ISO 时间戳）计算层级分析。from/to 缺失或不可解析时返回 null，
    /// 由调用方回退到明细直查。闭日读缓存，今天实时，非对齐边界日直查明细。
    /// </summary>
    public async Task<ContextLayerAnalysis?> GetAnalysisAsync(
        string? from,
        string? to,
        string? providerId,
        string? modelId,
        CancellationToken ct = default)
    {
        if (!DateTimeOffset.TryParse(from, out var fromDate) || !DateTimeOffset.TryParse(to, out var toDate))
            return null;

        var fromInstant = fromDate.UtcDateTime;
        var toInstant = toDate.UtcDateTime;
        if (toInstant <= fromInstant)
            return BuildAnalysis([], providerId, modelId);

        var payloads = new List<ContextLayerRollupRow>();
        var fromDay = fromInstant.Date;
        var toDay = toInstant.Date;
        var fromAligned = fromInstant.TimeOfDay == TimeSpan.Zero;
        // toISOString 只精确到毫秒（…T23:59:59.999Z），按 1ms 容差识别"覆盖到日末"
        var toCoversDayEnd = toDay.AddDays(1) - toInstant <= TimeSpan.FromMilliseconds(1);

        if (fromDay == toDay)
        {
            if (fromAligned && toCoversDayEnd)
            {
                await AddFullDayRangeAsync(payloads, fromDay, toDay.AddDays(1), ct);
            }
            else
            {
                await AddPartialRangeAsync(payloads, fromInstant, toInstant, ct);
            }
        }
        else
        {
            if (!fromAligned)
                await AddPartialRangeAsync(payloads, fromInstant, fromDay.AddDays(1), ct);

            var fullStart = fromAligned ? fromDay : fromDay.AddDays(1);
            var fullEnd = toCoversDayEnd ? toDay.AddDays(1) : toDay;
            if (fullStart < fullEnd)
                await AddFullDayRangeAsync(payloads, fullStart, fullEnd, ct);

            if (!toCoversDayEnd)
                await AddPartialRangeAsync(payloads, toDay, toInstant, ct);
        }

        return BuildAnalysis(payloads, providerId, modelId);
    }

    private async Task AddFullDayRangeAsync(
        List<ContextLayerRollupRow> payloads,
        DateTime startDay,
        DateTime endDayExclusive,
        CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var closedEnd = endDayExclusive < today ? endDayExclusive : today;
        if (startDay < closedEnd)
            payloads.AddRange(await GetClosedDaysAsync(startDay, closedEnd, ct));
        if (today >= startDay && today < endDayExclusive)
            payloads.AddRange(await GetLiveTodayAsync(ct));
    }

    private async Task AddPartialRangeAsync(
        List<ContextLayerRollupRow> payloads,
        DateTime startInstant,
        DateTime endInstant,
        CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);

        // 历史部分直接明细聚合（不缓存）；与 [今天, 明天) 的交集走实时路径
        var pastEnd = endInstant < today ? endInstant : today;
        if (startInstant < pastEnd)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            payloads.AddRange(await AggregateRangeAsync(db, startInstant, pastEnd, ct));
        }

        var liveStart = startInstant > today ? startInstant : today;
        var liveEnd = endInstant < tomorrow ? endInstant : tomorrow;
        if (liveStart < liveEnd)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            payloads.AddRange(await AggregateRangeAsync(db, liveStart, liveEnd, ct));
        }
    }

    /// <summary>已结束 UTC 日的 rollup；缺失日期按需构建（与 Token 聚合缓存相互独立）。</summary>
    public async Task<IReadOnlyList<ContextLayerRollupRow>> GetClosedDaysAsync(
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
        var entities = await db.ContextLayerDailyRollups.AsNoTracking()
            .Where(r => string.Compare(r.DayUtc, startDay) >= 0 && string.Compare(r.DayUtc, endDay) < 0)
            .ToListAsync(ct);
        return entities
            .Select(e => new ContextLayerRollupRow(
                e.DayUtc,
                e.LayerName,
                e.LayerOrder,
                e.LayerRole,
                e.ProviderId,
                e.ModelId,
                DeserializePayload(e.PayloadJson)))
            .ToList();
    }

    /// <summary>当前 UTC 日的实时 rollup（不落缓存）。</summary>
    public async Task<IReadOnlyList<ContextLayerRollupRow>> GetLiveTodayAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await AggregateRangeAsync(db, today, today.AddDays(1), ct);
    }

    private async Task EnsureBuiltAsync(DateTime startUtcDate, DateTime endUtcDateExclusive, CancellationToken ct)
    {
        await _buildGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var builtDays = await DailyCacheUtility.LoadBuiltDaysAsync(
                db,
                DailyCacheUtility.ContextLayerCacheKey,
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
                "[ContextLayerRollup] Built closed-day cache {Start}..{EndExclusive} runs={Runs} rows={Rows}",
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
        IReadOnlyList<ContextLayerRollupRow> rows,
        CancellationToken ct)
    {
        var days = DailyCacheUtility.EnumerateDays(chunkStart, chunkEndExclusive)
            .Select(DailyCacheUtility.FormatDay)
            .ToList();
        var builtAt = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ContextLayerDailyRollups.RemoveRange(
            db.ContextLayerDailyRollups.Where(r => days.Contains(r.DayUtc)));
        db.ContextLayerDailyRollups.AddRange(rows.Select(r => new ContextLayerDailyRollupEntity
        {
            DayUtc = r.DayUtc,
            LayerName = r.LayerName,
            LayerOrder = r.LayerOrder,
            LayerRole = r.LayerRole,
            ProviderId = r.ProviderId,
            ModelId = r.ModelId,
            PayloadJson = JsonSerializer.Serialize(r.Payload, PayloadJsonOpts),
            BuiltAtUtc = builtAt,
        }));
        db.StatsDailyCacheDays.AddRange(days.Select(day => new StatsDailyCacheDayEntity
        {
            CacheKey = DailyCacheUtility.ContextLayerCacheKey,
            DayUtc = day,
            BuiltAtUtc = builtAt,
        }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<List<ContextLayerRollupRow>> AggregateRangeAsync(
        PlatformDbContext db,
        DateTime startUtc,
        DateTime endUtcExclusive,
        CancellationToken ct)
    {
        var startText = DailyCacheUtility.ToSqliteUtcText(startUtc);
        var endText = DailyCacheUtility.ToSqliteUtcText(endUtcExclusive);

        var raw = await db.ContextLayerMetricEvents
            .FromSqlInterpolated($"""
                SELECT * FROM context_layer_metric_events
                WHERE occurred_at_utc >= {startText} AND occurred_at_utc < {endText}
                """)
            .AsNoTracking()
            .Select(e => new LayerRaw(
                e.OccurredAtUtc,
                e.LayerName,
                e.LayerOrder,
                e.LayerRole,
                e.ProviderId,
                e.ModelId,
                e.TokenCount,
                e.RawUtf8Bytes,
                e.GzipBytes,
                e.EstimatedCacheHitTokens,
                e.EstimatedCacheMissTokens,
                e.EstimatedCacheHitRate,
                e.IsChanged,
                e.ChangeReason,
                e.ContentHash))
            .ToListAsync(ct);

        return raw
            .GroupBy(e => (
                Day: e.OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd"),
                e.LayerName,
                e.LayerOrder,
                e.LayerRole,
                e.ProviderId,
                e.ModelId))
            .Select(g => new ContextLayerRollupRow(
                g.Key.Day,
                g.Key.LayerName,
                g.Key.LayerOrder,
                g.Key.LayerRole,
                g.Key.ProviderId,
                g.Key.ModelId,
                new ContextLayerRollupPayload(
                    g.LongCount(),
                    g.Sum(e => e.TokenCount),
                    g.Sum(e => e.RawUtf8Bytes),
                    g.Sum(e => e.GzipBytes),
                    g.Sum(e => e.EstimatedCacheHitTokens),
                    g.Sum(e => e.EstimatedCacheMissTokens),
                    g.Sum(e => e.EstimatedCacheHitRate ?? 0d),
                    g.Count(e => e.EstimatedCacheHitRate.HasValue),
                    g.Count(e => e.IsChanged),
                    g.Select(e => e.TokenCount).ToArray(),
                    g.Where(e => e.EstimatedCacheHitRate.HasValue)
                        .Select(e => e.EstimatedCacheHitRate!.Value)
                        .ToArray(),
                    g.Select(e => e.ContentHash).Distinct(StringComparer.Ordinal).ToArray(),
                    g.Where(e => !string.IsNullOrWhiteSpace(e.ChangeReason))
                        .GroupBy(e => e.ChangeReason!)
                        .ToDictionary(rg => rg.Key, rg => rg.LongCount()))))
            .ToList();
    }

    private static ContextLayerAnalysis BuildAnalysis(
        IReadOnlyList<ContextLayerRollupRow> rows,
        string? providerId,
        string? modelId)
    {
        var filtered = rows
            .Where(r => MatchesDimension(r.ProviderId, providerId) && MatchesDimension(r.ModelId, modelId))
            .ToList();

        var merged = filtered
            .GroupBy(r => (r.LayerName, r.LayerOrder, r.LayerRole))
            .Select(g => new
            {
                Key = g.Key,
                Payload = MergePayloads(g.Select(r => r.Payload)),
            })
            .ToList();

        var totalEvents = merged.Sum(m => m.Payload.Calls);
        var totalTokens = merged.Sum(m => m.Payload.TokenCount);
        var totalRawUtf8Bytes = merged.Sum(m => m.Payload.RawUtf8Bytes);
        var totalGzipBytes = merged.Sum(m => m.Payload.GzipBytes);

        var layers = merged
            .OrderBy(m => m.Key.LayerOrder)
            .ThenBy(m => m.Key.LayerName)
            .Select(m =>
            {
                var p = m.Payload;
                var cacheRates = p.HitRates;
                return new ContextLayerAnalysisRow(
                    m.Key.LayerName,
                    m.Key.LayerOrder,
                    m.Key.LayerRole,
                    p.Calls,
                    p.TokenCount,
                    p.RawUtf8Bytes,
                    p.GzipBytes,
                    RoundRatio(p.GzipBytes > 0 ? (double)p.RawUtf8Bytes / p.GzipBytes : 0),
                    RoundRatio(totalTokens > 0 ? (double)p.TokenCount / totalTokens : 0),
                    p.Calls == 0 ? 0 : Math.Round((double)p.TokenCount / p.Calls, 2),
                    Median(p.TokenCounts.Select(v => (double)v)),
                    Percentile(p.TokenCounts.Select(v => (double)v), 95),
                    p.EstimatedHitTokens,
                    p.EstimatedMissTokens,
                    p.HitRateCount == 0 ? 0 : RoundRatio(p.HitRateSum / p.HitRateCount),
                    Median(cacheRates),
                    p.ChangeCount,
                    RoundRatio(p.Calls == 0 ? 0 : (double)p.ChangeCount / p.Calls),
                    p.Hashes.Length,
                    p.ChangeReasons
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new ContextLayerReasonCount(kv.Key, kv.Value))
                        .ToList());
            })
            .ToList();

        return new ContextLayerAnalysis(totalEvents, totalTokens, totalRawUtf8Bytes, totalGzipBytes, layers);
    }

    private static ContextLayerRollupPayload MergePayloads(IEnumerable<ContextLayerRollupPayload> payloads)
    {
        var list = payloads.ToList();
        return new ContextLayerRollupPayload(
            list.Sum(p => p.Calls),
            list.Sum(p => p.TokenCount),
            list.Sum(p => p.RawUtf8Bytes),
            list.Sum(p => p.GzipBytes),
            list.Sum(p => p.EstimatedHitTokens),
            list.Sum(p => p.EstimatedMissTokens),
            list.Sum(p => p.HitRateSum),
            list.Sum(p => p.HitRateCount),
            list.Sum(p => p.ChangeCount),
            list.SelectMany(p => p.TokenCounts).ToArray(),
            list.SelectMany(p => p.HitRates).ToArray(),
            list.SelectMany(p => p.Hashes).Distinct(StringComparer.Ordinal).ToArray(),
            list.SelectMany(p => p.ChangeReasons)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)));
    }

    private static bool MatchesDimension(string? rowValue, string? filter)
        => string.IsNullOrWhiteSpace(filter)
            || (rowValue is not null && string.Equals(rowValue, filter, StringComparison.Ordinal));

    private static ContextLayerRollupPayload DeserializePayload(string json)
        => JsonSerializer.Deserialize<ContextLayerRollupPayload>(json) ?? EmptyPayload;

    private static readonly ContextLayerRollupPayload EmptyPayload = new(
        0, 0, 0, 0, 0, 0, 0d, 0, 0, [], [], [], []);

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            return 0;
        var mid = sorted.Length / 2;
        var value = sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2
            : sorted[mid];
        return Math.Round(value, 6);
    }

    internal static double Percentile(IEnumerable<double> values, int percentile)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            return 0;
        var rank = (percentile / 100.0) * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return Math.Round(sorted[lower], 6);
        var weight = rank - lower;
        return Math.Round(sorted[lower] * (1 - weight) + sorted[upper] * weight, 6);
    }

    internal static double RoundRatio(double value) => Math.Round(value, 6);

    private sealed record LayerRaw(
        DateTimeOffset OccurredAtUtc,
        string LayerName,
        int LayerOrder,
        string LayerRole,
        string? ProviderId,
        string? ModelId,
        long TokenCount,
        long RawUtf8Bytes,
        long GzipBytes,
        long EstimatedCacheHitTokens,
        long EstimatedCacheMissTokens,
        double? EstimatedCacheHitRate,
        bool IsChanged,
        string? ChangeReason,
        string ContentHash);
}
