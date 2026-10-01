using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>定位契约：线名冻结、Ref 的来源版本约束、引用的版本有效性。</summary>
public sealed class LocateContractTests
{
    [Fact]
    public void LocatorKindLineNames_AreFrozenAndUnknownIsRejected()
    {
        Assert.Equal(
            ["ref", "css", "xpath", "text", "role", "label", "placeholder", "alt-text", "title", "test-id"],
            Enum.GetValues<DesktopLocatorKind>().Select(DesktopLocatorKindWire.NameOf).ToArray());

        Assert.True(DesktopLocatorKindWire.TryParse("alt-text", out var altText));
        Assert.Equal(DesktopLocatorKind.AltText, altText);

        // 严格解析：不知道的线名不猜测、不回退到默认策略。
        Assert.False(DesktopLocatorKindWire.TryParse("shadow-piercing", out _));
        Assert.False(DesktopLocatorKindWire.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopLocatorKindWire.NameOf((DesktopLocatorKind)99));
    }

    [Fact]
    public void ReferenceLocator_MustCarryThePageVersionItCameFrom()
    {
        var target = new DesktopPageTarget("ctx-1", "page-1");
        var reference = new DesktopLocator(DesktopLocatorKind.Ref, "e1");

        // 没说明引用来自哪一版 ⇒ 接收方无法判定是否失效 ⇒ 构造期拒绝。
        Assert.Throws<ArgumentException>(() => new BrowserLocateRequest(target, reference));
        Assert.Throws<ArgumentException>(
            () => new BrowserLocateRequest(target, reference, DesktopPageVersion.Unknown));

        // 带上来源版本即可（非 Ref 策略则不需要）。
        _ = new BrowserLocateRequest(target, reference, DesktopPageVersion.Require(7));
        _ = new BrowserLocateRequest(target, new DesktopLocator(DesktopLocatorKind.Css, "button"));
    }

    [Fact]
    public void Locator_AndElementRef_ValidateTheirInputs()
    {
        Assert.Throws<ArgumentException>(() => new DesktopLocator(DesktopLocatorKind.Css, "  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopLocator(DesktopLocatorKind.Css, "a", nth: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BrowserLocateRequest(
                new DesktopPageTarget("c", "p"), new DesktopLocator(DesktopLocatorKind.Css, "a"), maxResults: 0));

        // 元素引用必须带有效版本：没有版本的 Ref 无法判断是否已失效。
        Assert.Throws<ArgumentException>(
            () => new DesktopElementRef("e1", "button", DesktopPageVersion.Unknown));
        var element = new DesktopElementRef(
            "e1", "input", DesktopPageVersion.Require(3), role: "checkbox", name: "同意", isChecked: true);

        Assert.True(element.IsChecked);
        Assert.Equal(3, element.PageVersion.Value);
        Assert.Equal("e1 <input> v3", element.ToString());
    }

    [Fact]
    public void LocateResult_DistinguishesEmptyFromTruncated()
    {
        var target = new DesktopPageTarget("ctx-1", "page-1");
        var locator = new DesktopLocator(DesktopLocatorKind.Text, "提交");

        var empty = new DesktopLocateResult(target, locator, [], truncated: false, DesktopPageVersion.Require(2));
        Assert.True(empty.IsEmpty);
        Assert.False(empty.Truncated);

        // 命中 0 个与「被截断」是两件事：前者要换策略/等待，后者要看下一页结果。
        var truncated = new DesktopLocateResult(
            target,
            locator,
            [new DesktopElementRef("e1", "button", DesktopPageVersion.Require(2))],
            truncated: true,
            DesktopPageVersion.Require(2));
        Assert.False(truncated.IsEmpty);
        Assert.True(truncated.Truncated);
    }
}
