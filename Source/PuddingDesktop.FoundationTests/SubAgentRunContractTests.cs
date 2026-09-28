using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>
/// DS-14 subagent-runs slice: paging stays inside Core's 1-500 clamp, the sub-session identity is kept
/// distinct from the run id, and archive degradation is surfaced rather than smoothed over.
/// </summary>
public sealed class SubAgentRunContractTests
{
    private static SubAgentRun Run(string id = "run-1", string status = "succeeded", string completedAt = "2026-09-27T00:00:05Z",
        long durationMs = 5000, string error = "") => new(
        id, "parent-1", "sub-1", "default", "agent-1", "global:general-assistant", status,
        "2026-09-27T00:00:00Z", completedAt, durationMs, 3, 7, error);

    [Fact]
    public void LimitsStayInsideCoresOwnPagingContract()
    {
        Assert.Equal(1, SubAgentRunText.MinLimit);
        Assert.Equal(500, SubAgentRunText.MaxLimit);
        Assert.Equal(1, SubAgentRunText.ClampLimit(0));
        Assert.Equal(500, SubAgentRunText.ClampLimit(10_000));

        var normalized = SubAgentRunText.Normalize(SubAgentRunFilter.Default with
        {
            Limit = 0, Offset = -5, ParentSessionId = "  p-1  ",
        });
        Assert.Equal(1, normalized.Limit);
        Assert.Equal(0, normalized.Offset);
        Assert.Equal("p-1", normalized.ParentSessionId);

        Assert.Empty(SubAgentRunText.Validate(SubAgentRunFilter.Default));
        Assert.Contains("1–500", SubAgentRunText.Validate(SubAgentRunFilter.Default with { Limit = 501 }).Single(), StringComparison.Ordinal);
        Assert.Contains("偏移量", SubAgentRunText.Validate(SubAgentRunFilter.Default with { Offset = -1 }).Single(), StringComparison.Ordinal);
        // Core 按字符串筛状态，所以界面不限制状态取值（只做大小写无关的展示翻译）。
        Assert.Empty(SubAgentRunText.Validate(SubAgentRunFilter.Default with { Status = "budget_exhausted" }));
        Assert.Empty(SubAgentRunText.Validate(SubAgentRunFilter.Default with { Status = "something-new" }));
    }

    [Fact]
    public void PagingReportsWhetherThereIsMore()
    {
        Assert.Contains("没有匹配", SubAgentRunPage.Empty.PageText, StringComparison.Ordinal);
        Assert.False(SubAgentRunPage.Empty.CanGoBack);
        Assert.False(SubAgentRunPage.Empty.CanGoForward);

        var page = new SubAgentRunPage([], 100, 20, 20);
        Assert.True(page.CanGoBack);
        Assert.True(page.CanGoForward);
        Assert.Contains("第 2 页", page.PageText, StringComparison.Ordinal);
        Assert.Contains("共 100 次运行", page.PageText, StringComparison.Ordinal);

        var last = page with { Offset = 80 };
        Assert.True(last.CanGoBack);
        Assert.False(last.CanGoForward);
    }

    [Fact]
    public void RunTextDistinguishesFinishedRunsAndKeepsErrorsVisible()
    {
        var running = Run(status: "running", completedAt: "");
        Assert.False(running.Finished);
        Assert.Contains("未结束", running.TimeText, StringComparison.Ordinal);
        Assert.Equal("运行中", running.StatusText);
        Assert.False(running.HasError);

        var failed = Run(status: "failed", error: "boom");
        Assert.True(failed.Finished);
        Assert.True(failed.HasError);
        Assert.Equal("失败", failed.StatusText);
        Assert.Contains("→", failed.TimeText, StringComparison.Ordinal);
        Assert.Contains("5 s", failed.CountsText, StringComparison.Ordinal);
        Assert.Contains("轮次 3", failed.CountsText, StringComparison.Ordinal);

        Assert.Equal("预算耗尽", SubAgentRunText.DescribeStatus("budget_exhausted"));
        Assert.Equal("被中断", SubAgentRunText.DescribeStatus("interrupted"));
        Assert.Equal("状态未知", SubAgentRunText.DescribeStatus(null));
        Assert.Equal("SomethingNew", SubAgentRunText.DescribeStatus("SomethingNew"));
        Assert.Contains("budget_exhausted", SubAgentRunText.Statuses);
    }

    [Fact]
    public void DetailSurfacesDegradationAndEmptySectionsExplicitly()
    {
        var detail = new SubAgentRunDetail(Run(), "the task", "the output",
            new Dictionary<string, string> { ["conscious"] = "deepseek-chat" },
            new Dictionary<string, string> { ["traceId"] = "t-1" }, 4, 2,
            SubAgentRunText.DescribeDegraded("2026-09-27T00:00:00Z", "disk busy", 3));

        Assert.True(detail.IsDegraded);
        Assert.Contains("曾丢弃 3 条事件", detail.DegradedText, StringComparison.Ordinal);
        Assert.Contains("disk busy", detail.DegradedText, StringComparison.Ordinal);
        Assert.Contains("conscious=deepseek-chat", detail.ProfileText, StringComparison.Ordinal);
        Assert.Contains("traceId=t-1", detail.TraceText, StringComparison.Ordinal);
        Assert.Equal("the task", detail.TaskText);
        Assert.Equal("the output", detail.OutputText);

        // 空段落明确写出来，而不是留白。
        var bare = detail with
        {
            Task = "", Output = "", LlmProfiles = new Dictionary<string, string>(),
            Trace = new Dictionary<string, string>(), DegradedText = "",
        };
        Assert.False(bare.IsDegraded);
        Assert.Equal("（没有任务描述）", bare.TaskText);
        Assert.Equal("（没有输出）", bare.OutputText);
        Assert.Contains("没有记录", bare.ProfileText, StringComparison.Ordinal);
        Assert.Contains("没有 trace", bare.TraceText, StringComparison.Ordinal);
        Assert.Equal("曾丢弃 0 条事件", SubAgentRunText.DescribeDegraded(null, null, 0));
    }

    [Fact]
    public void NoticesExplainSubSessionIdentityDegradationAndPayloadPreview()
    {
        // 子会话与 Run 的身份区别必须写在界面上：一个子会话可以对应多次运行。
        Assert.Contains("复用单位", SubAgentRunText.SubSessionNotice, StringComparison.Ordinal);
        Assert.Contains("runId 才是本次运行", SubAgentRunText.SubSessionNotice, StringComparison.Ordinal);
        Assert.Contains("时间线不完整", SubAgentRunText.DegradedNotice, StringComparison.Ordinal);
        Assert.Contains("200 字符预览", SubAgentRunText.PayloadNotice, StringComparison.Ordinal);

        var filter = SubAgentRunFilter.Default with { ParentSessionId = "p-1", Status = "failed" };
        Assert.Contains("父会话=p-1", filter.DescribeText, StringComparison.Ordinal);
        Assert.Contains("状态=failed", filter.DescribeText, StringComparison.Ordinal);
        Assert.Equal("未设置筛选（Core 返回的全部运行）", SubAgentRunFilter.Default.DescribeText);
    }
}
