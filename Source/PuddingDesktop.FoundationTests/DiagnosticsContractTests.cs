using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-14 diagnostics slice: filters stay inside Core's contract and redaction is described honestly.</summary>
public sealed class DiagnosticsContractTests
{
    [Fact]
    public void PageSizeAndPageNumberMatchCoresOwnClamp()
    {
        // Core 的查询把 PageSize 夹在 1–500、Page 至少 1。
        Assert.Equal(1, DiagnosticsText.ClampPageSize(0));
        Assert.Equal(1, DiagnosticsText.ClampPageSize(-5));
        Assert.Equal(500, DiagnosticsText.ClampPageSize(10_000));
        Assert.Equal(100, DiagnosticsText.ClampPageSize(100));

        var normalized = DiagnosticsText.Normalize(RuntimeTimelineFilter.Default with
        {
            Page = 0, PageSize = 9999, SortOrder = "sideways", DisplayMode = "pretty", SessionId = "  s-1  ",
        });
        Assert.Equal(1, normalized.Page);
        Assert.Equal(500, normalized.PageSize);
        // 未知排序/模式回落到 Core 的默认值，而不是把非法值传下去。
        Assert.Equal("desc", normalized.SortOrder);
        Assert.Equal("raw", normalized.DisplayMode);
        Assert.Equal("s-1", normalized.SessionId);
    }

    [Fact]
    public void ValidationRejectsWhatCoreWouldReject()
    {
        Assert.Empty(DiagnosticsText.Validate(RuntimeTimelineFilter.Default));
        Assert.Contains("页码", DiagnosticsText.Validate(RuntimeTimelineFilter.Default with { Page = 0 }).Single(), StringComparison.Ordinal);
        Assert.Contains("1–500", DiagnosticsText.Validate(RuntimeTimelineFilter.Default with { PageSize = 501 }).Single(), StringComparison.Ordinal);
        // 状态取值必须是 Core 的事件状态之一。
        Assert.Empty(DiagnosticsText.Validate(RuntimeTimelineFilter.Default with { Status = "failed" }));
        // 大小写不敏感：Core 按字符串筛选，所以 FAILED 是合法取值而不是错误。
        Assert.Empty(DiagnosticsText.Validate(RuntimeTimelineFilter.Default with { Status = "FAILED" }));
        Assert.Contains("状态取值", DiagnosticsText.Validate(RuntimeTimelineFilter.Default with { Status = "exploded" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void TimelinePageReportsPagingAndEmptiness()
    {
        Assert.Equal(0, RuntimeTimelinePage.Empty.PageCount);
        Assert.False(RuntimeTimelinePage.Empty.CanGoBack);
        Assert.False(RuntimeTimelinePage.Empty.CanGoForward);
        Assert.Contains("没有匹配", RuntimeTimelinePage.Empty.PageText, StringComparison.Ordinal);

        var page = new RuntimeTimelinePage([], 2, 100, 250);
        Assert.Equal(3, page.PageCount);
        Assert.True(page.CanGoBack);
        Assert.True(page.CanGoForward);
        Assert.Contains("第 2/3 页", page.PageText, StringComparison.Ordinal);
        Assert.Contains("共 250 条", page.PageText, StringComparison.Ordinal);
        Assert.False((page with { Page = 3 }).CanGoForward);
        // 只有一页时两侧都不可翻。
        var single = page with { Page = 1, Total = 40 };
        Assert.Equal(1, single.PageCount);
        Assert.False(single.CanGoBack);
        Assert.False(single.CanGoForward);
        // 末页不能继续往后翻。
        Assert.False((page with { Page = 3 }).CanGoForward);
        Assert.True((page with { Page = 3 }).CanGoBack);
    }

    [Fact]
    public void EntryTextUsesOnlyFieldsCoreProvides()
    {
        var entry = new RuntimeTimelineEntry("id-1", "activity", "session_state", "chat.stream.delta", "failed",
            "s-1", "agent-1", "run-1", "trace-1", "corr-1",
            DateTimeOffset.UtcNow.AddSeconds(-3), DateTimeOffset.UtcNow, 250,
            "summary", "boom", new Dictionary<string, string> { ["token"] = "***REDACTED***" });

        Assert.Equal("失败", entry.StatusText);
        Assert.Contains("session_state", entry.IdentityText, StringComparison.Ordinal);
        Assert.Contains("250 ms", entry.IdentityText, StringComparison.Ordinal);
        Assert.Contains("会话 s-1", entry.CorrelationText, StringComparison.Ordinal);
        Assert.Contains("Trace trace-1", entry.CorrelationText, StringComparison.Ordinal);
        Assert.True(entry.HasError);

        // 没有关联 ID 时明确说明，而不是显示空白。
        var bare = entry with { SessionId = "", RunId = "", TraceId = "", AgentInstanceId = "" };
        Assert.Contains("没有关联 ID", bare.CorrelationText, StringComparison.Ordinal);
        Assert.False((entry with { Error = "" }).HasError);
    }

    [Fact]
    public void FiltersAndDurationsRenderReadably()
    {
        Assert.Equal("未设置筛选（最近的事件）", RuntimeTimelineFilter.Default.DescribeText);
        var filter = RuntimeTimelineFilter.Default with { SessionId = "s-1", Status = "failed", Component = "subagent" };
        Assert.Contains("会话=s-1", filter.DescribeText, StringComparison.Ordinal);
        Assert.Contains("组件=subagent", filter.DescribeText, StringComparison.Ordinal);

        Assert.Equal("999 ms", DiagnosticsText.DescribeDuration(999));
        Assert.Equal("1.5 s", DiagnosticsText.DescribeDuration(1500));
        Assert.Equal("2 min", DiagnosticsText.DescribeDuration(120_000));
        Assert.Equal("已开始", DiagnosticsText.DescribeStatus("started"));
        Assert.Equal("已取消", DiagnosticsText.DescribeStatus("cancelled"));
        Assert.Equal("状态未知", DiagnosticsText.DescribeStatus(null));
        Assert.Equal("SomethingNew", DiagnosticsText.DescribeStatus("SomethingNew"));
        Assert.Equal("健康", DiagnosticsText.DescribeHealth("healthy"));
        Assert.Equal("降级", DiagnosticsText.DescribeHealth("Degraded"));
    }

    [Fact]
    public void OverviewAggregatesComponentsAndStatesTheHealthCaveat()
    {
        var healthy = new RuntimeComponentHealth("session_state", "Healthy", 10, 10, 0, 0, 0, DateTimeOffset.UtcNow);
        var unhealthy = new RuntimeComponentHealth("subagent", "Unhealthy", 4, 1, 3, 2, 1, null);
        var overview = new DiagnosticsOverview([healthy, unhealthy], RuntimeTimelinePage.Empty with { Total = 3 });

        Assert.Equal(2, overview.Components.Count);
        Assert.Equal(14, overview.Started);
        Assert.Equal(11, overview.Succeeded);
        Assert.Equal(3, overview.Failed);
        Assert.Equal(2, overview.Retried);
        Assert.Equal(1, overview.Cancelled);
        Assert.Equal(1, overview.UnhealthyCount);
        Assert.Contains("组件 2", overview.HeadlineText, StringComparison.Ordinal);
        Assert.Contains("未健康 1", overview.HeadlineText, StringComparison.Ordinal);
        Assert.Equal(0, DiagnosticsOverview.Empty.Started);

        Assert.True(unhealthy.HasFailures);
        Assert.False(healthy.HasFailures);
        // 计数是事实、健康标签是 Core 的判断：两者不一致时以计数为准并标出。
        Assert.True((healthy with { FailedCount = 1 }).HasFailures);
        Assert.Contains("失败 0", healthy.CountsText, StringComparison.Ordinal);
        Assert.Equal("没有时间戳", unhealthy.LastSeenText);

        Assert.Contains("不是本机探针", DiagnosticsText.HealthNotice, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", DiagnosticsText.RedactionNotice, StringComparison.Ordinal);
        // 这条差异必须写在界面上：自由文本里的密钥形态串不会被清洗。
        Assert.Contains("不会", DiagnosticsText.FreeTextGapNotice, StringComparison.Ordinal);
        Assert.Contains("sk-", DiagnosticsText.FreeTextGapNotice, StringComparison.Ordinal);
        Assert.Contains("四个来源", DiagnosticsText.EmptyTimelineNotice, StringComparison.Ordinal);
    }
}
