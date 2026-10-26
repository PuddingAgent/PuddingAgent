using PuddingBrowser.Abstractions;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// **pageVersion 递增判据**（<see cref="PageVersionPolicy"/>）的行为断言。
///
/// 守的是一个会**静默出错**的不变量：pageVersion 是"旧元素引用是否还有效"的唯一判据，
/// 一旦该递增却没递增，旧 ref 会在新页面上被判为仍然有效——不报错，只是点到别的东西。
/// </summary>
public sealed class PageVersionPolicyTests
{
    private const string FeedUrl = "https://www.douyin.com/?recommend=1";

    private const string ProfileUrl =
        "https://www.douyin.com/user/self?from_tab_name=main&showTab=favorite_collection";

    [Fact]
    public void SameDocumentNavigationBumpsVersion()
    {
        // 实测形态：推荐流 → 我的。URL 变了、且没有真实导航在进行。
        Assert.True(PageVersionPolicy.BumpsOnSourceChange(FeedUrl, ProfileUrl, navigationInFlight: false));
    }

    [Fact]
    public void RealNavigationDoesNotDoubleCount()
    {
        // 真实导航由 NavigationCompleted 负责递增（那里还能区分成功/失败）。
        Assert.False(PageVersionPolicy.BumpsOnSourceChange(FeedUrl, ProfileUrl, navigationInFlight: true));
    }

    [Fact]
    public void UnchangedUrlDoesNotBump()
    {
        // SourceChanged 也可能因同址重复上报触发：没有变化就不该让版本漂移。
        Assert.False(PageVersionPolicy.BumpsOnSourceChange(FeedUrl, FeedUrl, navigationInFlight: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingNewUrlDoesNotBump(string? newUrl)
    {
        // 拿不到地址就没有证据说明文档换了 —— 不猜。
        Assert.False(PageVersionPolicy.BumpsOnSourceChange(FeedUrl, newUrl, navigationInFlight: false));
    }

    [Fact]
    public void QueryStringChangeBumpsVersion()
    {
        // 同路由换查询串同样可能整块换内容（抖音的 showTab 就是这样）。
        const string withTab = "https://www.douyin.com/user/self?showTab=collection";
        const string withoutTab = "https://www.douyin.com/user/self?showTab=favorite";

        Assert.True(PageVersionPolicy.BumpsOnSourceChange(withoutTab, withTab, navigationInFlight: false));
    }

    [Fact]
    public void UrlComparisonIsOrdinalNotCaseInsensitive()
    {
        // 逐字节比较：大小写不同的 URL 是两个不同的文档地址，不能当成"没变"。
        Assert.True(PageVersionPolicy.BumpsOnSourceChange(
            "https://example.com/A", "https://example.com/a", navigationInFlight: false));
    }
}
