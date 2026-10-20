namespace PuddingDesktop.FoundationTests;

using System.Net;
using PuddingDesktop.Foundation;

/// <summary>
/// 面板内容拉取（<see cref="PanelCanvasFeed"/>）的纯逻辑契约。
///
/// 为什么这层要有自己的测试：Shell 侧只有「定时器 + HttpClient + WebView2」，
/// 真正会出错的地方全在这里 —— URL 拼错、JSON 少字段被默默兜底、revision 比较写反。
/// 三种错误的表现都是「画布不刷新」，没有异常、没有日志，肉眼极难定位。
/// </summary>
public sealed class PanelCanvasFeedTests
{
    private const string ValidBody = """
        {"panelId":"main","title":"汽车架构图","revision":7,"updatedAt":"2026-10-20T09:00:00Z","content":"<p>hi</p>"}
        """;

    [Fact]
    public void BuildUri_points_at_the_canvas_route()
    {
        var uri = PanelCanvasFeed.BuildUri(new Uri("http://127.0.0.1:8080"), "main");

        Assert.Equal("http://127.0.0.1:8080/api/panels/main", uri.ToString());
    }

    [Fact]
    public void BuildUri_escapes_a_hostile_panel_id()
    {
        // panelId 正常只允许字母数字 . _ -，但这一层不该假设上游校验过：
        // 未转义的 "../x" 会把请求打到别的路由上。
        var uri = PanelCanvasFeed.BuildUri(new Uri("http://127.0.0.1:8080"), "../admin");

        Assert.Equal("http://127.0.0.1:8080/api/panels/..%2Fadmin", uri.ToString());
    }

    [Fact]
    public void BuildUri_rejects_a_relative_address()
    {
        Assert.Throws<ArgumentException>(() => PanelCanvasFeed.BuildUri(new Uri("/api", UriKind.Relative), "main"));
    }

    [Fact]
    public void TryParse_reads_the_snapshot()
    {
        Assert.True(PanelCanvasFeed.TryParse(ValidBody, out var item, out var error));
        Assert.Null(error);
        Assert.Equal("main", item.PanelId);
        Assert.Equal("汽车架构图", item.Title);
        Assert.Equal(7, item.Revision);
        Assert.Equal("<p>hi</p>", item.Content);
    }

    [Fact]
    public void TryParse_normalizes_a_blank_title_to_null()
    {
        const string body = """
            {"panelId":"main","title":"   ","revision":1,"content":"x"}
            """;

        Assert.True(PanelCanvasFeed.TryParse(body, out var item, out _));
        Assert.Null(item.Title);
    }

    [Fact]
    public void TryParse_rejects_a_missing_revision_instead_of_defaulting_it()
    {
        // 兜底成 0 的后果：RequiresRerender 永远为 false，画布静默冻结。
        const string body = """
            {"panelId":"main","content":"x"}
            """;

        Assert.False(PanelCanvasFeed.TryParse(body, out _, out var error));
        Assert.Contains("revision", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_rejects_missing_content()
    {
        const string body = """
            {"panelId":"main","revision":3}
            """;

        Assert.False(PanelCanvasFeed.TryParse(body, out _, out var error));
        Assert.Contains("content", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_rejects_a_non_object_body()
    {
        Assert.False(PanelCanvasFeed.TryParse("[1,2,3]", out _, out var error));
        Assert.Contains("object", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_reports_invalid_json_instead_of_throwing()
    {
        Assert.False(PanelCanvasFeed.TryParse("{not json", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RequiresRerender_is_false_only_for_the_identical_revision()
    {
        Assert.False(PanelCanvasFeed.RequiresRerender(7, 7));
        Assert.True(PanelCanvasFeed.RequiresRerender(7, 8));
    }

    [Fact]
    public void RequiresRerender_also_fires_when_the_revision_goes_backwards()
    {
        // Core 重启后存储清空，下一次发布又是 revision 1。
        // 若这里写成「大于才重渲染」，画布会一直停在重启前的旧内容上。
        Assert.True(PanelCanvasFeed.RequiresRerender(5, 1));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, PanelCanvasFeedStatus.Fetched)]
    [InlineData(HttpStatusCode.NotFound, PanelCanvasFeedStatus.NotPublished)]
    [InlineData(HttpStatusCode.InternalServerError, PanelCanvasFeedStatus.Unreachable)]
    [InlineData(HttpStatusCode.Unauthorized, PanelCanvasFeedStatus.Unreachable)]
    public void StatusForHttpCode_splits_published_from_absent_from_broken(
        HttpStatusCode code, PanelCanvasFeedStatus expected)
        => Assert.Equal(expected, PanelCanvasFeed.StatusForHttpCode(code));

    [Fact]
    public void DescribeFailure_always_produces_something_a_human_can_read()
    {
        Assert.Contains("推送", PanelCanvasFeed.DescribeFailure(PanelCanvasFeedStatus.NotPublished, null));
        Assert.Contains("超时", PanelCanvasFeed.DescribeFailure(PanelCanvasFeedStatus.Unreachable, "超时"));
        Assert.False(string.IsNullOrWhiteSpace(
            PanelCanvasFeed.DescribeFailure(PanelCanvasFeedStatus.Unreachable, null)));
    }
}
