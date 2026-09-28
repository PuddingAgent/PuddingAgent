namespace PuddingDesktop.Foundation;

/// <summary>One aggregated usage row: a UTC day × ledger source × provider × model.</summary>
public sealed record TokenUsageRow(
    string DayUtc, string Source, string ProviderId, string ModelId,
    long PromptTokens, long CompletionTokens, long CacheHitTokens, long CacheMissTokens, long RequestCount,
    decimal InputCost, decimal CacheHitCost, decimal OutputCost, decimal TotalCost)
{
    public long TotalTokens => PromptTokens + CompletionTokens;
    public string DayText => DayUtc.Length >= 10 ? DayUtc[..10] : DayUtc;
    public string ModelText => $"{ProviderId} / {(ModelId.Length == 0 ? "（未记录模型）" : ModelId)}";
    public string LineText =>
        $"{DayText} · {Source} · {ModelText} · 输入 {PromptTokens} · 输出 {CompletionTokens} · " +
        $"缓存命中 {CacheHitTokens} / 未命中 {CacheMissTokens} · 请求 {RequestCount} · {TokenUsageText.Money(TotalCost)}";
}

/// <summary>
/// The summary card's aggregate. Totals are computed here from rows Core returned, never invented.
/// </summary>
public sealed record TokenUsageSummary(
    TokenUsageWindow Window, IReadOnlyList<TokenUsageRow> Rows, bool IncludesLiveToday, DateTimeOffset LoadedAtUtc)
{
    public static TokenUsageSummary Empty { get; } = new(TokenUsageWindow.Last7Days, [], false, DateTimeOffset.UtcNow);

    public long PromptTokens => Rows.Sum(row => row.PromptTokens);
    public long CompletionTokens => Rows.Sum(row => row.CompletionTokens);
    public long TotalTokens => Rows.Sum(row => row.TotalTokens);
    public long CacheHitTokens => Rows.Sum(row => row.CacheHitTokens);
    public long CacheMissTokens => Rows.Sum(row => row.CacheMissTokens);
    public long RequestCount => Rows.Sum(row => row.RequestCount);
    public decimal TotalCost => Rows.Sum(row => row.TotalCost);
    public int DayCount => Rows.Select(row => row.DayText).Distinct(StringComparer.Ordinal).Count();
    public int ProviderCount => Rows.Select(row => row.ProviderId).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Cache hit ratio over all prompt-side tokens; zero when nothing was recorded.</summary>
    public double CacheHitRatio
    {
        get
        {
            var total = CacheHitTokens + CacheMissTokens;
            return total == 0 ? 0 : (double)CacheHitTokens / total;
        }
    }

    public string HeadlineText =>
        $"Token 合计 {TotalTokens}（输入 {PromptTokens} · 输出 {CompletionTokens}）· 请求 {RequestCount} · " +
        $"成本 {TokenUsageText.Money(TotalCost)}";
    public string DetailText =>
        $"覆盖 {DayCount} 个 UTC 日 · {ProviderCount} 个服务商 · 缓存命中率 {CacheHitRatio:P1}" +
        (IncludesLiveToday ? " · 含今日实时数据（未落缓存）" : " · 仅已结束的 UTC 日");

    /// <summary>Per-day totals for the table, newest first.</summary>
    public IReadOnlyList<(string Day, long Tokens, long Requests, decimal Cost)> PerDay => Rows
        .GroupBy(row => row.DayText, StringComparer.Ordinal)
        .Select(group => (
            Day: group.Key,
            Tokens: group.Sum(row => row.TotalTokens),
            Requests: group.Sum(row => row.RequestCount),
            Cost: group.Sum(row => row.TotalCost)))
        .OrderByDescending(entry => entry.Day, StringComparer.Ordinal)
        .ToArray();
}

public enum TokenUsageWindowKind { Today, Last7Days, Last30Days, ThisMonth }

/// <summary>A window resolved to concrete UTC day bounds so the page and Core agree on the range.</summary>
public sealed record TokenUsageWindow(TokenUsageWindowKind Kind, DateTime StartUtcDate, DateTime EndUtcDateExclusive)
{
    public static TokenUsageWindow Last7Days => For(TokenUsageWindowKind.Last7Days, DateTime.UtcNow);
    public static TokenUsageWindow Last30Days => For(TokenUsageWindowKind.Last30Days, DateTime.UtcNow);
    public static TokenUsageWindow Today => For(TokenUsageWindowKind.Today, DateTime.UtcNow);

    public string DescribeText => Kind switch
    {
        TokenUsageWindowKind.Today => $"今天（UTC {StartUtcDate:yyyy-MM-dd}）",
        TokenUsageWindowKind.Last7Days => $"最近 7 个 UTC 日（{StartUtcDate:yyyy-MM-dd} 起）",
        TokenUsageWindowKind.Last30Days => $"最近 30 个 UTC 日（{StartUtcDate:yyyy-MM-dd} 起）",
        _ => $"本月（UTC {StartUtcDate:yyyy-MM}）"
    };

    public static TokenUsageWindow For(TokenUsageWindowKind kind, DateTime utcNow)
    {
        var today = utcNow.Date;
        return kind switch
        {
            TokenUsageWindowKind.Today => new TokenUsageWindow(kind, today, today.AddDays(1)),
            TokenUsageWindowKind.Last7Days => new TokenUsageWindow(kind, today.AddDays(-6), today.AddDays(1)),
            TokenUsageWindowKind.Last30Days => new TokenUsageWindow(kind, today.AddDays(-29), today.AddDays(1)),
            _ => new TokenUsageWindow(kind, new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                today.AddDays(1))
        };
    }

    public static IReadOnlyList<TokenUsageWindowKind> Kinds { get; } =
    [
        TokenUsageWindowKind.Today, TokenUsageWindowKind.Last7Days,
        TokenUsageWindowKind.Last30Days, TokenUsageWindowKind.ThisMonth
    ];
}

/// <summary>One raw usage event from the ledger.</summary>
public sealed record TokenUsageEvent(
    long Id, string WorkspaceId, string SessionId, string ProviderId, string ModelId,
    long PromptTokens, long CompletionTokens, long TotalTokens, long CacheHitTokens, long CacheMissTokens,
    string SourceType, long OccurredAtUnixMs)
{
    public string LineText =>
        $"#{Id} · {TokenUsageText.DescribeUnixMs(OccurredAtUnixMs)} · {ProviderId}/{(ModelId.Length == 0 ? "（未记录模型）" : ModelId)} · " +
        $"输入 {PromptTokens} · 输出 {CompletionTokens} · 合计 {TotalTokens}" +
        $" · 缓存 {CacheHitTokens}/{CacheMissTokens} · 来源 {SourceType}";
    public string ScopeText =>
        $"工作区 {(WorkspaceId.Length == 0 ? "（未记录）" : WorkspaceId)} · 会话 {(SessionId.Length == 0 ? "（未记录）" : SessionId)}";
    public bool HasCacheDetail => CacheHitTokens >= 0 && CacheMissTokens >= 0;
}

public sealed record TokenUsageLedgerPage(IReadOnlyList<TokenUsageEvent> Items, int TotalCount, int Page, int PageSize)
{
    public static TokenUsageLedgerPage Empty { get; } = new([], 0, 1, 50);
    public int PageCount => PageSize <= 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;
    public bool CanGoBack => Page > 1;
    public bool CanGoForward => Page < PageCount;
    public string PageText => TotalCount == 0
        ? "没有匹配的用量事件"
        : $"第 {Page}/{PageCount} 页 · 共 {TotalCount} 条 · 每页 {PageSize}";
}

public sealed record TokenUsageEventFilter(
    string WorkspaceId, string SessionId, string ProviderId, string ModelId, int Page, int PageSize)
{
    public static TokenUsageEventFilter Default { get; } = new("", "", "", "", 1, 50);
    public string DescribeText
    {
        get
        {
            var parts = new List<string>();
            if (WorkspaceId.Length > 0) parts.Add($"工作区={WorkspaceId}");
            if (SessionId.Length > 0) parts.Add($"会话={SessionId}");
            if (ProviderId.Length > 0) parts.Add($"服务商={ProviderId}");
            if (ModelId.Length > 0) parts.Add($"模型={ModelId}");
            return parts.Count == 0 ? "未设置筛选（最近的用量事件）" : string.Join(" · ", parts);
        }
    }
}

public interface ITokenUsageSettings
{
    Task<TokenUsageSummary> LoadSummaryAsync(TokenUsageWindow window, CancellationToken cancellationToken = default);
    Task<TokenUsageLedgerPage> ListEventsAsync(TokenUsageEventFilter filter, CancellationToken cancellationToken = default);
}

public static class TokenUsageText
{
    public const string LedgerNotice =
        "统计口径同时覆盖两个账本（llm_gateway_usage_events 与 TokenUsageEvents）：只算其中一个会漏量。";

    public const string LiveTodayNotice =
        "今天的数据是**实时聚合**（不落缓存），已结束的 UTC 日走聚合缓存；两者口径一致但来源不同。";

    public const string DateRangeNotice =
        "登记限制：Core 的用量事件查询**不支持时间窗**——它按 DateTimeOffset 比较，SQLite 无法翻译该谓词" +
        "（DateTimeOffset 的比较与排序都不支持），因此本页只按工作区/会话/服务商/模型筛选与分页。" +
        "概览卡片的窗口统计走日聚合（按 UTC 文本比较），不受这个限制影响。";

    public const string CostNotice =
        "成本是账本里按模型单价算好的金额（含缓存命中价），不是界面按当前价目表重算的。";

    public static string Money(decimal amount) => amount == 0 ? "0" : amount.ToString("0.######");

    public static string DescribeWindow(TokenUsageWindowKind kind) => kind switch
    {
        TokenUsageWindowKind.Today => "今天",
        TokenUsageWindowKind.Last7Days => "最近 7 天",
        TokenUsageWindowKind.Last30Days => "最近 30 天",
        _ => "本月"
    };

    public static string DescribeUnixMs(long unixMs) => unixMs <= 0
        ? "时间未知"
        : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 500);

    public static TokenUsageEventFilter Normalize(TokenUsageEventFilter filter) => filter with
    {
        WorkspaceId = filter.WorkspaceId.Trim(),
        SessionId = filter.SessionId.Trim(),
        ProviderId = filter.ProviderId.Trim(),
        ModelId = filter.ModelId.Trim(),
        Page = Math.Max(1, filter.Page),
        PageSize = ClampPageSize(filter.PageSize),
    };

    public static IReadOnlyList<string> Validate(TokenUsageEventFilter filter)
    {
        var errors = new List<string>();
        if (filter.Page < 1) errors.Add("页码至少为 1。");
        if (filter.PageSize is < 1 or > 500) errors.Add("每页条数必须在 1–500 之间。");
        return errors;
    }

    /// <summary>
    /// The time filter is deliberately absent from the contract; if a caller ever asks for one this is the
    /// reason string to show rather than silently ignoring the bound.
    /// </summary>
    public static string ExplainMissingDateRange() => DateRangeNotice;
}
