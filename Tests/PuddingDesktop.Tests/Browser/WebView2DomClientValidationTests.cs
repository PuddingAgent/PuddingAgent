using PuddingBrowser.Abstractions;
using PuddingBrowser.WebView2;
using Xunit;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// B2 §6.2 的 **C# 侧**可进程内验证部分：`WebView2DomClient.ValidateLocator`。
/// 真正的匹配语义在 <c>LocateScript</c>（内嵌 JS，需真实 DOM）⇒ 其行为属真机/外部验收；
/// 但"拒绝明显非法的定位器"这一层是纯托管逻辑，可以在这里钉住。
/// </summary>
public sealed class WebView2DomClientValidationTests
{
    [Fact]
    public void AnEmptyLocatorValueIsRejected()
    {
        var ex = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(
                new Locator { Kind = LocatorKind.Css, Value = "   " },
                pageVersion: 1,
                pageToken: "token"));

        Assert.Contains("Locator value is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellFormedLocatorIsAccepted()
    {
        // 正例（对照组）：否则"上面那条会抛"可能只是因为整段逻辑永远抛。
        WebView2DomClient.ValidateLocator(
            new Locator { Kind = LocatorKind.Css, Value = ".item" },
            pageVersion: 1,
            pageToken: "token");
    }
}