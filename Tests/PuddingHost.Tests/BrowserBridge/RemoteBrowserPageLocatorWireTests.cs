using Pudding.Contracts.Desktop;
using PuddingHost.BrowserBridge;
using PuddingBrowser.Abstractions;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// 定位 kind 的**线缆契约**：宿主（生产者）把 <see cref="LocatorKind"/> 编译进
/// <c>BrowserLocatorDescriptor.Kind</c>，Desktop（消费者）用
/// <c>BrowserBridgeCommandAdapter.TryMapLocator</c> → <see cref="DesktopLocatorKindWire.TryParse"/>
/// 按**线名**（kebab-case）严格解析，不猜测、不回退。
///
/// 缺陷背景：生产者曾用 <c>locator.Kind.ToString()</c> 送枚举名（"AltText"），
/// 消费者的线名表里只有 "alt-text" ⇒ 十种定位**全部**被判为"未注册"，
/// 页面已能导航、快照也正常，但 locate / interact 一律 fail closed。
/// 本测试用**接收端的真实解析器**做断言，因此能真正取红。
/// </summary>
public sealed class RemoteBrowserPageLocatorWireTests
{
    public static TheoryData<LocatorKind> AllLocatorKinds()
    {
        var data = new TheoryData<LocatorKind>();
        foreach (var kind in Enum.GetValues<LocatorKind>())
        {
            data.Add(kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllLocatorKinds))]
    public void EmittedKindIsParseableByTheReceiversWireParser(LocatorKind kind)
    {
        var wire = RemoteBrowserPage.LocatorWireName(kind);

        // 1) 接收端解析器必须接受它（这是被测的真实行为）。
        Assert.True(
            DesktopLocatorKindWire.TryParse(wire, out var parsed),
            $"receiver rejects wire name '{wire}' for {kind}");

        // 2) 且必须解析回**同一个**策略（不能落到别的分支上）。
        Assert.Equal(kind.ToString(), parsed.ToString());

        // 3) 线名一律小写 kebab：出现大写说明又退回了枚举名。
        Assert.Equal(wire.ToLowerInvariant(), wire);
    }

    /// <summary>
    /// **真实发送路径**：<c>ToDescriptor</c> 就是写进线上 <c>BrowserLocatorDescriptor.Kind</c> 的那一处。
    /// 只测 <see cref="RemoteBrowserPage.LocatorWireName"/> 本身是不够的 —— 实测把 <c>ToDescriptor</c>
    /// 改回 <c>locator.Kind.ToString()</c> 时，那个用例**仍然是绿的**（变异没走到被测路径）。
    /// 因此这里直接断言描述符，才能捕到真正的历史回归形态。
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLocatorKinds))]
    public void DescriptorSentOverTheWireCarriesTheWireName(LocatorKind kind)
    {
        var descriptor = RemoteBrowserPage.ToDescriptor(new Locator { Kind = kind, Value = "probe" });

        Assert.True(
            DesktopLocatorKindWire.TryParse(descriptor.Kind, out var parsed),
            $"receiver rejects wire name '{descriptor.Kind}' for {kind}");
        Assert.Equal(kind.ToString(), parsed.ToString());
    }

    [Fact]
    public void EnumNamesAreNotWireNames_WhichIsWhyToStringMustNotBeUsed()
    {
        // 这条是**取红哨兵**：一旦有人把 Kind 改回 locator.Kind.ToString()，
        // 接收端就会拒绝全部十种定位 —— 这里先把"枚举名不是线名"钉死。
        foreach (var kind in Enum.GetValues<LocatorKind>())
        {
            Assert.False(
                DesktopLocatorKindWire.TryParse(kind.ToString(), out _),
                $"'{kind}' must not be a valid wire name; the wire form is '{RemoteBrowserPage.LocatorWireName(kind)}'");
        }
    }

    [Theory]
    [InlineData(LocatorKind.Ref, "ref")]
    [InlineData(LocatorKind.Css, "css")]
    [InlineData(LocatorKind.XPath, "xpath")]
    [InlineData(LocatorKind.Text, "text")]
    [InlineData(LocatorKind.Role, "role")]
    [InlineData(LocatorKind.Label, "label")]
    [InlineData(LocatorKind.Placeholder, "placeholder")]
    [InlineData(LocatorKind.AltText, "alt-text")]
    [InlineData(LocatorKind.Title, "title")]
    [InlineData(LocatorKind.TestId, "test-id")]
    public void WireNamesMatchThePublishedKebabCaseContract(LocatorKind kind, string expected)
        => Assert.Equal(expected, RemoteBrowserPage.LocatorWireName(kind));

    /// <summary>
    /// 两个正交筛选必须**过线**：`includeHidden` / `inViewportOnly` 曾在 <c>ToDescriptor</c> 里
    /// 被整个漏掉，接收端只能拿到默认 <c>false</c>——调用方请求“只取视口内”，
    /// 实际拿回整个列表，而回执看起来完全正常（实测 908px 视口返回了 y=-346.6 与 y=1471.4）。
    /// </summary>
    [Fact]
    public void DescriptorSentOverTheWireCarriesTheViewportFilters()
    {
        var descriptor = RemoteBrowserPage.ToDescriptor(new Locator
        {
            Kind = LocatorKind.Css,
            Value = "probe",
            IncludeHidden = true,
            InViewportOnly = true
        });

        Assert.True(descriptor.IncludeHidden);
        Assert.True(descriptor.InViewportOnly);
    }

    /// <summary>反向对照：默认构造不得把两个筛选项悄悄打开（否则“没给”会变成“全要”）。</summary>
    [Fact]
    public void DescriptorDefaultsLeaveBothFiltersOff()
    {
        var descriptor = RemoteBrowserPage.ToDescriptor(new Locator { Kind = LocatorKind.Css, Value = "probe" });

        Assert.False(descriptor.IncludeHidden);
        Assert.False(descriptor.InViewportOnly);
    }
}
