namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 画布快照的**身份校验**契约：响应必须自证是请求的那块画布。
///
/// 这条边界容易被当成多余的防御而删掉。删掉之后不会编译失败、不会让既有测试变红，
/// 只会让「A 画布显示 B 的内容」变成一个无声的现场 —— 路由错配、代理缓存串号、
/// 存储串键都会产生这种响应，而画布本身看起来完全正常。
/// </summary>
public sealed class PanelCanvasFeedIdentityTests
{
    private const string MainBody = """
        {"panelId":"main","title":"部署验证","revision":7,"content":"<p>hi</p>"}
        """;

    [Fact]
    public void Accepts_the_snapshot_when_it_is_for_the_requested_panel()
    {
        Assert.True(PanelCanvasFeed.TryParse(MainBody, out var item, out var error, "main"));

        Assert.Null(error);
        Assert.Equal("main", item.PanelId);
        Assert.Equal("<p>hi</p>", item.Content);
    }

    [Fact]
    public void Rejects_a_snapshot_belonging_to_another_panel()
    {
        const string body = """
            {"panelId":"other","revision":2,"content":"<p>别的画布</p>"}
            """;

        Assert.False(PanelCanvasFeed.TryParse(body, out _, out var error, "main"));

        // 错误信息要能直接看懂「谁 vs 谁」，否则排查时还得去翻代码。
        Assert.Contains("other", error!, StringComparison.Ordinal);
        Assert.Contains("main", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Stays_permissive_when_no_panel_is_expected()
    {
        // 不传期望 id 时保持既有行为：留给「只想知道响应长什么样」的诊断调用方。
        // 渲染路径必须传 —— 这条断言不能被读成「校验可以省」。
        Assert.True(PanelCanvasFeed.TryParse(MainBody, out var item, out _, null));
        Assert.Equal("main", item.PanelId);
    }

    [Fact]
    public void Compares_panel_ids_case_sensitively()
    {
        // 两条路由是两个资源。改成 OrdinalIgnoreCase，/api/panels/Main 就能冒充 main。
        const string body = """
            {"panelId":"Main","revision":1,"content":"x"}
            """;

        Assert.False(PanelCanvasFeed.TryParse(body, out _, out _, "main"));
    }

    [Fact]
    public void Compares_against_the_trimmed_panel_id()
    {
        // 响应体里的 id 带空白是格式问题，不是身份问题：先归一化再比对，
        // 否则「多一个空格」会被误报成「另一块画布」。
        const string body = """
            {"panelId":" main ","revision":3,"content":"x"}
            """;

        Assert.True(PanelCanvasFeed.TryParse(body, out var item, out _, "main"));
        Assert.Equal("main", item.PanelId);
    }
}
