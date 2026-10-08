using PuddingBrowser.Abstractions;
using PuddingBrowser.WebView2;

namespace PuddingDesktop.Tests.Browser;

public sealed class WebView2DomClientTests
{
    /// <summary>驱动侧的页面作用域 token（ref 前缀的一部分）。</summary>
    private const string PageToken = "0123456789abcdef0123456789abcdef";

    private static Locator Ref(string value) => new() { Kind = LocatorKind.Ref, Value = value };

    [Fact]
    public void Normalize_ClampsSnapshotBudgets()
    {
        var normalized = WebView2DomClient.Normalize(new SnapshotOptions
        {
            MaxNodes = int.MaxValue,
            MaxTextLength = 1,
            MaxDepth = 0
        });

        Assert.Equal(10_000, normalized.MaxNodes);
        Assert.Equal(256, normalized.MaxTextLength);
        Assert.Equal(1, normalized.MaxDepth);
    }

    [Fact]
    public void ValidateLocator_AcceptsCurrentVersionRef()
        => WebView2DomClient.ValidateLocator(
            Ref($"p{PageToken}-v7-n12"), pageVersion: 7, pageToken: PageToken);

    [Fact]
    public void ValidateLocator_RejectsStaleRefWithStableCode()
    {
        var exception = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(
                Ref($"p{PageToken}-v6-n12"), pageVersion: 7, pageToken: PageToken));

        Assert.Equal("stale_element_reference", exception.Code);
    }

    [Fact]
    public void ValidateLocator_RejectsARefFromAnotherPage_EvenAtTheSameVersion()
    {
        // 设计点名的情况：ref 序号是**每页独立**生成的，所以「页面 A 的 v7-n3」与
        // 「页面 B 的 v7-n3」会同时存在。只比版本号时跨页引用会静默命中另一个页面的元素。
        var exception = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(
                Ref("pffffffffffffffffffffffffffffffff-v7-n3"), pageVersion: 7, pageToken: PageToken));

        Assert.Equal("stale_element_reference", exception.Code);

        // 提示必须只说「重新观察」，不得暗示可以重绑到同名节点。
        Assert.Contains("re-observe", exception.Message);
        Assert.Contains("never re-bound", exception.Message);
    }

    [Fact]
    public void ValidateLocator_RejectsTheLegacyUnscopedRefFormat()
    {
        // 旧格式没有页面作用域 ⇒ 无法证明它属于本页，必须拒绝（不做静默兼容层）。
        var exception = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(Ref("v7-n12"), pageVersion: 7, pageToken: PageToken));

        Assert.Equal("browser_invalid_arguments", exception.Code);
    }

    [Fact]
    public void ValidateLocator_DoesNotLeakThePageTokenInTheMessage()
    {
        var exception = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(
                Ref("pffffffffffffffffffffffffffffffff-v7-n3"), pageVersion: 7, pageToken: PageToken));

        Assert.DoesNotContain(PageToken, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.test/tasks/42", "https://example.test/tasks/*", true)]
    [InlineData("https://example.test/tasks/42", "*/TASKS/?2", true)]
    [InlineData("https://example.test/tasks/42", "https://other.test/*", false)]
    public void WildcardMatch_UsesStableCaseInsensitiveSemantics(
        string input, string pattern, bool expected)
        => Assert.Equal(expected, WebView2DomClient.WildcardMatch(input, pattern));

    [Fact]
    public void LocateAndInteractScripts_ReportFormValueAndSelectedValues()
    {
        // 表单回读是「输入是否写入成功」的唯一可靠回执（设计 §4.2/§6.2）。
        // ⚠️ 这里只钉**脚本文本**：真实执行需要 WebView2 与可见窗口（WebView2.Smoke 才有），
        // 因此这条断言防的是「有人把回读删掉」，不构成运行时验收。
        foreach (var script in new[] { WebView2DomClient.LocateScript, WebView2DomClient.InteractScript })
        {
            Assert.Contains("value:readValue(el)", script);
            Assert.Contains("selectedValues:readSelectedValues(el)", script);
            Assert.Contains("const readValue = el =>", script);
            Assert.Contains("const readSelectedValues = el =>", script);
        }
    }

    [Fact]
    public void LocateScripts_ExcludeHiddenElementsByDefault()
    {
        // 设计 §6.2：locate 默认 includeHidden=false；只排除**隐藏**（display/visibility/opacity/零尺寸），
        // 屏外但有尺寸的元素仍可定位（那是 inViewport，不是 hidden）。
        // ⚠️ 同样是脚本文本断言：真机过滤行为需 WebView2.Smoke 复核。
        Assert.Contains(
            "if (!locator.includeHidden) elements = elements.filter(el => visible(el));",
            WebView2DomClient.LocateScript);

        // 交互脚本有一份自己的 locate：过滤必须一致，否则"定位不歧义、交互却歧义"。
        Assert.Contains(
            "if(!locator.includeHidden)out=out.filter(visible);",
            WebView2DomClient.InteractScript);
    }

    [Fact]
    public void Scripts_ReportInViewportAndGateWritesOnIt()
    {
        // 设计 §6.2：visible 与 inViewport 是两个事实；写操作要求后者为真（先滚入可见区域并复检）。
        // ⚠️ 同样是脚本文本断言：真机滚动/复检行为需 WebView2.Smoke 复核。
        foreach (var script in new[] { WebView2DomClient.LocateScript, WebView2DomClient.InteractScript })
        {
            Assert.Contains("const inViewport = el =>", script);
            Assert.Contains("inViewport:inViewport(el)", script);
        }

        Assert.Contains("if (locator.inViewportOnly) elements = elements.filter(el => inViewport(el));", WebView2DomClient.LocateScript);

        // 写操作先滚入、再复检；仍不满足就明确拒绝（不是静默点一个看不见的节点）。
        Assert.Contains("if(mutating&&!inViewport(el)){try{el.scrollIntoView(", WebView2DomClient.InteractScript);
        Assert.Contains("browser_element_not_in_viewport", WebView2DomClient.InteractScript);
    }

    [Fact]
    public void ValidateLocator_RejectsFrameAndCompoundHasUntilSupported()
    {
        var exception = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(new Locator
            {
                Kind = LocatorKind.Css,
                Value = "button",
                Frame = new FrameSelector { Name = "payment" }
            }, pageVersion: 1, pageToken: PageToken));

        Assert.Equal("browser_operation_not_supported", exception.Code);
    }
}
