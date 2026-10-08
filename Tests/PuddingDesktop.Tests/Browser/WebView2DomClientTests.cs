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
    public void SnapshotScript_ScopesTheScan_AndNeverFallsBackToTheWholePage()
    {
        // 设计 §151：scope(locator/ref) —— 只读该元素子树。
        // 过期/未知 ref 必须**明确拒绝**：退化成正页扫描会让调用方以为拿到的是子树。
        // ⚠️ 脚本文本/结构断言：真机行为需 WebView2.Smoke 复核。
        var script = WebView2DomClient.SnapshotScript;

        Assert.Contains("input.scopeRef", script);
        Assert.Contains("stale_element_reference", script);

        // 结构断言：作用域解析失败必须**先返回**，然后才可能落到整页遍历。
        var scopeCheck = script.IndexOf("if (!root) return", StringComparison.Ordinal);
        var walk = script.IndexOf("if (root) visit(root, 0, 'main')", StringComparison.Ordinal);

        Assert.True(scopeCheck >= 0, "作用域 ref 找不到时必须立即返回错误");
        Assert.True(walk > scopeCheck, "整页/子树遍历必须在作用域检查**之后**");
    }

    [Fact]
    public void InteractScript_RejectsObstructedWriteTargets()
    {
        // 设计 §6.2：「隐藏/禁用/**遮挡**目标不按成功处理」。
        // 写操作前做命中测试；被别的节点挡住 ⇒ 明确拒绝，绝不"照点不误"。
        // ⚠️ 脚本文本断言：真机遮挡行为需 WebView2.Smoke 复核。
        var script = WebView2DomClient.InteractScript;

        Assert.Contains("browser_element_obstructed", script);
        Assert.Contains("elementFromPoint", script);
        // shadow DOM 里命中会返回宿主，直接用 document 会误判 ⇒ 必须问元素所在根。
        Assert.Contains("getRootNode()", script);

        // 结构断言（仅查关键词会被"守卫被禁用"骗过）：命中测试必须**真的在写操作守卫内**，
        // 且在 disabled 检查之前执行。
        var guard = script.IndexOf("if(mutating){", StringComparison.Ordinal);
        var hitTest = script.IndexOf("root.elementFromPoint(", StringComparison.Ordinal);
        var reject = script.IndexOf("browser_element_obstructed", StringComparison.Ordinal);
        var disabled = script.IndexOf("if(el.disabled)", StringComparison.Ordinal);

        Assert.True(guard >= 0, "写操作守卫 `if(mutating){` 必须存在（禁用守卫等于放弃遮挡判定）");
        Assert.True(hitTest > guard, "命中测试必须在写操作守卫内");
        Assert.True(reject > hitTest, "命中不通过必须在命中测试之后立即拒绝");
        Assert.True(disabled > reject, "遮挡判定必须发生在 disabled 检查之前（否则会先报禁用）");
    }

    [Fact]
    public void Scripts_KeepOnlyTheMinimalTextMatch_AndDistinguishFilteredZeroHits()
    {
        // 设计 §6.2：text 定位必须取**最小匹配节点**（否则祖先也含文本 ⇒ "返回整页"），
        // 且零命中要分清"真的没有"与"命中了但全被隐藏/视口筛掉"。
        // ⚠️ 脚本文本断言：真机行为需 WebView2.Smoke 复核。
        foreach (var script in new[] { WebView2DomClient.LocateScript, WebView2DomClient.InteractScript })
        {
            Assert.Contains("el.contains(other)", script);
            Assert.Contains("browser_locator_matches_filtered", script);
            Assert.Contains("matchedBeforeFilters", script);
        }

        // 定位脚本的最小节点规则只在 text 定位上生效。
        Assert.Contains("if (locator.kind === 'text')", WebView2DomClient.LocateScript);
        // 交互脚本与定位脚本共用同一套语义（否则"定位不歧义、交互却歧义"）。
        Assert.Contains("if(locator.kind==='text')", WebView2DomClient.InteractScript);
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
    public void ScrollScript_ImplementsTheTargetModel_AndReportsFactsInsteadOfGuessing()
    {
        // 设计 §7：目标模型 page/element/to_ref/to_top/to_bottom；返回目标容器、前后位置、
        // consumedDelta、atBoundary、contentChanged；"无内容加载"不是失败。
        // ⚠️ 脚本文本断言：真机滚动行为需 WebView2.Smoke 复核。
        var script = WebView2DomClient.ScrollScript;

        foreach (var mode in new[] { "page", "element", "to_ref", "to_top", "to_bottom" })
        {
            Assert.Contains($"'{mode}'", script);
        }

        // 元素滚动会先看元素自身、必要时上溯最近可滚动祖先（不盲目滚另一个区域）。
        Assert.Contains("nearestScrollable", script);

        // 结果事实：容器/前后位置/消耗位移/边界/内容变化。
        Assert.Contains("consumedDeltaX", script);
        Assert.Contains("consumedDeltaY", script);
        Assert.Contains("atBoundary", script);
        Assert.Contains("contentChanged", script);

        // 目标模型线名由 Abstractions 的 ScrollTargetWire 统一给出（驱动与 Core 共用一份）。
        Assert.Equal("page", ScrollTargetWire.NameOf(ScrollTarget.Page));
        Assert.Equal("to_ref", ScrollTargetWire.NameOf(ScrollTarget.ToRef));
        Assert.Equal("to_bottom", ScrollTargetWire.NameOf(ScrollTarget.ToBottom));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollTargetWire.NameOf((ScrollTarget)99));
    }

    [Fact]
    public void ScrollTargets_DescribeWhatTheScriptNeeds_AndElementTargetsRequireRefs()
    {
        // 元素类目标必须给 ref：脚本按宿主 ref 精确匹配，不做二次定位（否则"滚了另一个区域"）。
        Assert.Throws<BrowserOperationException>(() => WebView2DomClient.DescribeScroll(
            new ScrollOptions { Target = ScrollTarget.ToRef, Locator = new Locator { Kind = LocatorKind.Css, Value = ".x" } }));

        var described = WebView2DomClient.DescribeScroll(new ScrollOptions
        {
            Target = ScrollTarget.ToRef,
            Locator = new Locator { Kind = LocatorKind.Ref, Value = "p1-v2-n3" },
        });
        Assert.Equal(("to_ref", "p1-v2-n3", null, null), described);

        var document = WebView2DomClient.DescribeScroll(new ScrollOptions { DeltaY = 100 });
        Assert.Equal(("page", null, null, 100d), document);
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
