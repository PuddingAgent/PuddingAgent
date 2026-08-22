using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 按日缓存共用工具：cache_key 常量、UTC 日枚举、闭日完成标记读取。
/// </summary>
internal static class DailyCacheUtility
{
    public const string TokenUsageCacheKey = "token_usage";
    public const string ContextLayerCacheKey = "context_layer";

    public static string FormatDay(DateTime utcDate) => utcDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 与 Microsoft.Data.Sqlite 绑定 DateTimeOffset 的 TEXT 表示一致：
    /// "yyyy-MM-dd HH:mm:ss.FFFFFFF+00:00"（尾随零省略、整秒无小数段，偏移恒为 UTC）。
    /// EF Core SQLite 无法翻译 DateTimeOffset 参数的比较，范围查询必须按此格式做文本比较；
    /// F 格式的字典序与时间序一致（'+' &lt; '.' &lt; 数字），可正常使用 occurred_at_utc 索引。
    /// </summary>
    public static string ToSqliteUtcText(DateTime utc) =>
        utc.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture) + "+00:00";

    public static IEnumerable<DateTime> EnumerateDays(DateTime startUtc, DateTime endUtcExclusive)
    {
        for (var day = startUtc; day < endUtcExclusive; day = day.AddDays(1))
            yield return day;
    }

    /// <summary>把缺失日期折叠成连续区间，构建时按区间扫描事件表。</summary>
    public static IEnumerable<(DateTime RunStart, DateTime RunEndExclusive)> EnumerateMissingRuns(
        DateTime startUtc,
        DateTime endUtcExclusive,
        IReadOnlySet<string> builtDays)
    {
        DateTime? runStart = null;
        for (var day = startUtc; day < endUtcExclusive; day = day.AddDays(1))
        {
            var isBuilt = builtDays.Contains(FormatDay(day));
            if (!isBuilt)
            {
                runStart ??= day;
            }
            else if (runStart is not null)
            {
                yield return (runStart.Value, day);
                runStart = null;
            }
        }

        if (runStart is not null)
            yield return (runStart.Value, endUtcExclusive);
    }

    /// <summary>读取 cache_key 已完成标记并裁剪到 [startDay, endDay)（标记表很小，直接整表载入后内存过滤）。</summary>
    public static async Task<HashSet<string>> LoadBuiltDaysAsync(
        PlatformDbContext db,
        string cacheKey,
        DateTime startUtc,
        DateTime endUtcExclusive,
        CancellationToken ct)
    {
        var startDay = FormatDay(startUtc);
        var endDay = FormatDay(endUtcExclusive);
        var days = await db.StatsDailyCacheDays.AsNoTracking()
            .Where(d => d.CacheKey == cacheKey)
            .Select(d => d.DayUtc)
            .ToListAsync(ct);
        return days
            .Where(d => string.CompareOrdinal(d, startDay) >= 0 && string.CompareOrdinal(d, endDay) < 0)
            .ToHashSet(StringComparer.Ordinal);
    }
}
