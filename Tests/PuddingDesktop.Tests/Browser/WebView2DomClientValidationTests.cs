using PuddingBrowser.Abstractions;
using PuddingBrowser.WebView2;
using Xunit;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// B2 §6.2 的 **C# 侧**可进程内验证部分：`WebView2DomClient.ValidateLocator` 与 `Normalize`。
/// 真正的匹配语义在 <c>LocateScript</c>（内嵌 JS，需真实 DOM）⇒ 其行为属真机/外部验收；
/// 这里只钉住"明显非法的定位器被拒绝"与"选项被确定性夹取"这两层纯托管逻辑。
/// </summary>
public sealed class WebView2DomClientValidationTests
{
    private static Locator Css(string value) => new() { Kind = LocatorKind.Css, Value = value };

    [Fact]
    public void AnEmptyLocatorValueIsRejected()
    {
        var ex = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(Css("   "), pageVersion: 1, pageToken: "token"));

        Assert.Contains("Locator value is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellFormedLocatorIsAccepted()
    {
        // 正例（对照组）：否则"上面那条会抛"可能只是因为整段逻辑永远抛。
        WebView2DomClient.ValidateLocator(Css(".item"), pageVersion: 1, pageToken: "token");
    }

    [Fact]
    public void CompoundHasLocatorsAreRefusedAsNotAvailable()
    {
        var ex = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(
                new Locator { Kind = LocatorKind.Css, Value = ".card", Has = Css(".title") },
                pageVersion: 1,
                pageToken: "token"));

        Assert.Contains("not available", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceLocatorMustCarryARefShapedValue()
    {
        Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(
                new Locator { Kind = LocatorKind.Ref, Value = "not-a-ref" },
                pageVersion: 1,
                pageToken: "token"));

        // 注意：**未**在同一用例里断言"合法 ref 通过" —— 实测 `"e1"` 被本层拒绝（`Invalid browser ref format`），
        // 说明驱动侧要求的 ref 形状比"e1"更严（或与 pageToken/version 绑定）。合法形状待读 `BrowserRefRegex`
        // 的生成模式后单独钉住；此处只钉"畸形 ref 必被拒绝"这一条已核实的行为。
    }

    [Fact]
    public void MaxTextLengthIsClampedDeterministically()
    {
        Assert.Equal(256, WebView2DomClient.Normalize(new SnapshotOptions { MaxTextLength = 10 }).MaxTextLength);
        Assert.Equal(500_000, WebView2DomClient.Normalize(new SnapshotOptions { MaxTextLength = 9_999_999 }).MaxTextLength);
        Assert.Equal(200_000, WebView2DomClient.Normalize(new SnapshotOptions()).MaxTextLength);
    }
}