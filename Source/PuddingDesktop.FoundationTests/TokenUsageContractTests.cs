using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-15 usage slice: window bounds are resolved to concrete UTC days, totals are computed from the rows Core
/// returned, and the missing date range on the event query is stated rather than silently ignored.
/// </summary>
public sealed class TokenUsageContractTests
{
    private static TokenUsageRow Row(string day = "2026-09-27", string source = "llm_gateway",
        string provider = "deepseek", string model = "deepseek-chat", long prompt = 100, long completion = 20,
        long cacheHit = 30, long cacheMiss = 70, long requests = 2, decimal cost = 0.0123m) => new(
        day, source, provider, model, prompt, completion, cacheHit, cacheMiss, requests,
        0.005m, 0.002m, 0.0053m, cost);

    [Fact]
    public void WindowsResolveToConcreteUtcDayBounds()
    {
        var now = new DateTime(2026, 9, 27, 13, 45, 0, DateTimeKind.Utc);

        var today = TokenUsageWindow.For(TokenUsageWindowKind.Today, now);
        Assert.Equal(new DateTime(2026, 9, 27), today.StartUtcDate);
        Assert.Equal(new DateTime(2026, 9, 28), today.EndUtcDateExclusive);

        var week = TokenUsageWindow.For(TokenUsageWindowKind.Last7Days, now);
        // 最近 7 天包含今天，所以起点是 6 天前（不是 7 天前）。
        Assert.Equal(new DateTime(2026, 9, 21), week.StartUtcDate);
        Assert.Equal(new DateTime(2026, 9, 28), week.EndUtcDateExclusive);

        var month = TokenUsageWindow.For(TokenUsageWindowKind.Last30Days, now);
        Assert.Equal(new DateTime(2026, 8, 29), month.StartUtcDate);

        var thisMonth = TokenUsageWindow.For(TokenUsageWindowKind.ThisMonth, now);
        Assert.Equal(new DateTime(2026, 9, 1), thisMonth.StartUtcDate);
        Assert.Equal(new DateTime(2026, 9, 28), thisMonth.EndUtcDateExclusive);

        // 跨月/跨年边界：本月窗口的起点必须落在当月 1 日 UTC。
        var january = TokenUsageWindow.For(TokenUsageWindowKind.ThisMonth, new DateTime(2027, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2027, 1, 1), january.StartUtcDate);
        Assert.Contains("2027-01", january.DescribeText, StringComparison.Ordinal);

        Assert.Equal(4, TokenUsageWindow.Kinds.Count);
        Assert.Contains("最近 7 个 UTC 日", week.DescribeText, StringComparison.Ordinal);
        Assert.Equal("本月", TokenUsageText.DescribeWindow(TokenUsageWindowKind.ThisMonth));
    }

    [Fact]
    public void TotalsAreComputedFromRowsNotInvented()
    {
        var window = TokenUsageWindow.Last7Days;
        var summary = new TokenUsageSummary(window,
        [
            Row(day: "2026-09-26", prompt: 100, completion: 20, cacheHit: 30, cacheMiss: 70, requests: 2, cost: 0.01m),
            Row(day: "2026-09-27", provider: "openai", model: "", prompt: 50, completion: 10, cacheHit: 0, cacheMiss: 50, requests: 1, cost: 0.02m),
            Row(day: "2026-09-27", provider: "deepseek", prompt: 10, completion: 5, cacheHit: 15, cacheMiss: 0, requests: 3, cost: 0.03m),
        ], IncludesLiveToday: true, DateTimeOffset.UtcNow);

        Assert.Equal(160, summary.PromptTokens);
        Assert.Equal(35, summary.CompletionTokens);
        Assert.Equal(195, summary.TotalTokens);
        Assert.Equal(6, summary.RequestCount);
        Assert.Equal(45, summary.CacheHitTokens);
        Assert.Equal(120, summary.CacheMissTokens);
        Assert.Equal(0.06m, summary.TotalCost);
        Assert.Equal(2, summary.DayCount);
        Assert.Equal(2, summary.ProviderCount);
        // 缓存命中率按输入侧全部 token 计算。
        Assert.Equal(45d / 165d, summary.CacheHitRatio, 6);
        Assert.Contains("Token 合计 195", summary.HeadlineText, StringComparison.Ordinal);
        Assert.Contains("含今日实时数据", summary.DetailText, StringComparison.Ordinal);

        // 按日汇总：新的一天在前，且金额按日相加。
        var perDay = summary.PerDay;
        Assert.Equal(2, perDay.Count);
        Assert.Equal("2026-09-27", perDay[0].Day);
        Assert.Equal(75, perDay[0].Tokens);
        Assert.Equal(4, perDay[0].Requests);
        Assert.Equal(0.05m, perDay[0].Cost);
        Assert.Equal("2026-09-26", perDay[1].Day);

        // 空集不能产生假数据，也不能除以 0。
        var empty = TokenUsageSummary.Empty;
        Assert.Equal(0, empty.TotalTokens);
        Assert.Equal(0, empty.CacheHitRatio);
        Assert.Empty(empty.PerDay);
        Assert.Equal(0, empty.TotalCost);
        Assert.Empty(empty.Rows);
    }

    [Fact]
    public void RowsAndEventsRenderOnlyFieldsCoreProvides()
    {
        var row = Row(model: "");
        Assert.Equal("2026-09-27", row.DayText);
        Assert.Equal(120, row.TotalTokens);
        Assert.Contains("（未记录模型）", row.ModelText, StringComparison.Ordinal);
        Assert.Contains("deepseek / （未记录模型）", row.LineText, StringComparison.Ordinal);
        Assert.Contains("请求 2", row.LineText, StringComparison.Ordinal);

        var usageEvent = new TokenUsageEvent(7, "", "", "deepseek", "deepseek-chat", 10, 5, 15, 3, 7, "chat", 1_792_000_000_000);
        Assert.Contains("deepseek/deepseek-chat", usageEvent.LineText, StringComparison.Ordinal);
        Assert.Contains("合计 15", usageEvent.LineText, StringComparison.Ordinal);
        Assert.Contains("（未记录）", usageEvent.ScopeText, StringComparison.Ordinal);
        Assert.True(usageEvent.HasCacheDetail);

        // 未上报缓存明细（-1）时不会被当成 0 显示。
        var noCache = usageEvent with { CacheHitTokens = -1, CacheMissTokens = -1 };
        Assert.False(noCache.HasCacheDetail);
        Assert.Contains("时间未知", (usageEvent with { OccurredAtUnixMs = 0 }).LineText, StringComparison.Ordinal);
        Assert.Contains("2026-", TokenUsageText.DescribeUnixMs(1_792_000_000_000), StringComparison.Ordinal);
    }

    [Fact]
    public void EventPagingAndFiltersMatchCoresRepository()
    {
        Assert.Equal(1, TokenUsageText.ClampPageSize(0));
        Assert.Equal(500, TokenUsageText.ClampPageSize(10_000));

        var normalized = TokenUsageText.Normalize(TokenUsageEventFilter.Default with
        {
            Page = 0, PageSize = 0, SessionId = "  s-1  ",
        });
        Assert.Equal(1, normalized.Page);
        Assert.Equal(1, normalized.PageSize);
        Assert.Equal("s-1", normalized.SessionId);

        Assert.Empty(TokenUsageText.Validate(TokenUsageEventFilter.Default));
        Assert.Contains("页码", TokenUsageText.Validate(TokenUsageEventFilter.Default with { Page = 0 }).Single(), StringComparison.Ordinal);
        Assert.Contains("1–500", TokenUsageText.Validate(TokenUsageEventFilter.Default with { PageSize = 501 }).Single(), StringComparison.Ordinal);

        var page = new TokenUsageLedgerPage([], 120, 2, 50);
        Assert.Equal(3, page.PageCount);
        Assert.True(page.CanGoBack);
        Assert.True(page.CanGoForward);
        Assert.Contains("第 2/3 页", page.PageText, StringComparison.Ordinal);
        Assert.False((page with { Page = 3 }).CanGoForward);
        Assert.Contains("没有匹配", TokenUsageLedgerPage.Empty.PageText, StringComparison.Ordinal);

        var filter = TokenUsageEventFilter.Default with { WorkspaceId = "default", ProviderId = "deepseek" };
        Assert.Contains("工作区=default", filter.DescribeText, StringComparison.Ordinal);
        Assert.Equal("未设置筛选（最近的用量事件）", TokenUsageEventFilter.Default.DescribeText);
    }

    [Fact]
    public void NoticesRecordTheTwoLedgersLiveTodayAndTheMissingDateRange()
    {
        Assert.Contains("两个账本", TokenUsageText.LedgerNotice, StringComparison.Ordinal);
        Assert.Contains("实时聚合", TokenUsageText.LiveTodayNotice, StringComparison.Ordinal);
        // 时间窗缺失是 Core 的真实限制，必须说明原因而不是悄悄忽略。
        Assert.Contains("不支持时间窗", TokenUsageText.DateRangeNotice, StringComparison.Ordinal);
        Assert.Contains("SQLite", TokenUsageText.DateRangeNotice, StringComparison.Ordinal);
        Assert.Contains("日聚合", TokenUsageText.DateRangeNotice, StringComparison.Ordinal);
        Assert.Equal(TokenUsageText.DateRangeNotice, TokenUsageText.ExplainMissingDateRange());
        // 成本是账本算好的金额，不是界面重算。
        Assert.Contains("不是界面按当前价目表重算", TokenUsageText.CostNotice, StringComparison.Ordinal);
        Assert.Equal("0", TokenUsageText.Money(0));
        Assert.Equal("0.0123", TokenUsageText.Money(0.0123m));
        Assert.Equal("今天", TokenUsageText.DescribeWindow(TokenUsageWindowKind.Today));
    }
}
