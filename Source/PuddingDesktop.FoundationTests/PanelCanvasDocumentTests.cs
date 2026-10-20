namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 画布文档包装的契约：嵌入宿主 vs 导出的差别**只应该**是 CSP 与标题。
///
/// 这条边界容易在重构里被抹平（两处都调同一个 Wrap 最省事），而抹平之后表现是
/// 「下载到本地打开什么都看不见」——一个不会让任何编译或既有测试失败的静默缺陷。
/// </summary>
public sealed class PanelCanvasDocumentTests
{
    private const string Content = "<div id=\"probe\">内容</div>";

    [Fact]
    public void Embedding_document_carries_the_sandbox_policy()
    {
        var html = PanelCanvasDocument.WrapForEmbedding(Content, WorkbenchColorScheme.Light);

        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains(PanelCanvasDocument.EmbeddingContentSecurityPolicy, html);
    }

    [Fact]
    public void Embedding_policy_allows_local_images_but_still_blocks_everything_else()
    {
        var policy = PanelCanvasDocument.EmbeddingContentSecurityPolicy;

        // 本地图片通道必须放行，否则 pudding-image:// 的 img 全会被 CSP 拦掉。
        Assert.Contains("pudding-image:", policy, StringComparison.Ordinal);
        Assert.Contains("img-src data:", policy, StringComparison.Ordinal);
        // 但不能因此把 img-src 放宽到任意来源。
        Assert.DoesNotContain("img-src *", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("img-src http", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedding_document_injects_the_annotation_layer_but_export_does_not()
    {
        // 导出文件是给用户拿走的静态快照；把一层交互壳塞进去只会让它在浏览器里
        // 莫名多出一层透明遮罩。
        var embedded = PanelCanvasDocument.WrapForEmbedding(Content, WorkbenchColorScheme.Light);
        var exported = PanelCanvasDocument.WrapForExport(Content, WorkbenchColorScheme.Light);

        Assert.Contains("puddingAnnotation", embedded, StringComparison.Ordinal);
        Assert.Contains("data-pudding-annotation", embedded, StringComparison.Ordinal);
        Assert.DoesNotContain("puddingAnnotation", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void Annotation_layer_offers_a_per_item_remove_button()
    {
        // 画错一个框不必清空重画。这里只钉住几个**一旦丢失就不会报错**的关键标记：
        // 少了它们标注依然能画、测试依然全绿，但删除功能静默消失。
        var html = PanelCanvasDocument.WrapForEmbedding(Content, WorkbenchColorScheme.Light);

        Assert.Contains("data-pudding-remove", html, StringComparison.Ordinal);
        Assert.Contains("items.splice(index, 1)", html, StringComparison.Ordinal);
        // 父层是 pointer-events:none，不显式恢复 auto 的话按钮根本点不到。
        Assert.Contains("pointer-events:auto", html, StringComparison.Ordinal);
        // 不 stopPropagation 的话，点删除会顺带开始画一个新框。
        Assert.Contains("ev.stopPropagation()", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Annotation_layer_exposes_drawing_tools_and_compositing()
    {
        // 这些标记一旦丢失不会编译失败，但工具或烧录会静默消失：
        // 少了 compose，用户画的笔迹永远进不了像素，形式 1（任意标记）直接失效。
        var html = PanelCanvasDocument.WrapForEmbedding(Content, WorkbenchColorScheme.Light);

        Assert.Contains("setTool:", html, StringComparison.Ordinal);
        Assert.Contains("compose: compose", html, StringComparison.Ordinal);
        Assert.Contains("function drawVector", html, StringComparison.Ordinal);
        Assert.Contains("kind: 'scribble'", html, StringComparison.Ordinal);
        // 涂鸦/箭头走 SVG；div 只能画轴对齐矩形，画不出斜线和任意轨迹。
        Assert.Contains("data-pudding-vector", html, StringComparison.Ordinal);
        // 橡皮擦本质上是「撤销上一次」，所以撤销栈是工具的前提。
        Assert.Contains("function pushUndo", html, StringComparison.Ordinal);
        // 文字输入不能依赖 window.prompt：WebView2 默认拦下原生对话框。
        Assert.Contains("data-pudding-text-input", html, StringComparison.Ordinal);
        // 拖拽中的实时预览必须跟随指针。
        Assert.Contains(
            "addEventListener('pointermove', onMove)", html, StringComparison.Ordinal);
        // 烧录导出。
        Assert.Contains("toDataURL('image/png')", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Canvas_take_returns_items_verbatim_so_scribbles_keep_their_points()
    {
        // 早期实现按 kind 二选一输出，会把 scribble/text/arrow 静默降级成 point，
        // 笔迹与文字一起丢失，而且不会报任何错。
        var html = PanelCanvasDocument.WrapForEmbedding(Content, WorkbenchColorScheme.Light);

        Assert.Contains("items: items", html, StringComparison.Ordinal);
        Assert.DoesNotContain("? { kind: 'box', x1: it.x1", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Base_css_defaults_to_border_box_so_a_border_cannot_overflow_the_canvas()
    {
        // 实测缺陷：img 用 width:100% + border:1px，content-box 下比容器宽 2px，
        // 画布底部立刻出现横向滚动条，而内容看上去毫无异常。
        var embedded = PanelCanvasDocument.WrapForEmbedding(Content, WorkbenchColorScheme.Light);
        var exported = PanelCanvasDocument.WrapForExport(Content, WorkbenchColorScheme.Light);

        Assert.Contains("*,*::before,*::after{box-sizing:border-box;}", embedded, StringComparison.Ordinal);
        Assert.Contains("*,*::before,*::after{box-sizing:border-box;}", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void Annotation_layer_follows_scroll_and_resize()
    {
        // 遮罩是 position:fixed：滚动后不重定位，用户会按旧矩形算坐标，
        // 标注静默落到错误位置。这条只守「监听还在」，行为仍需人工点验。
        var html = PanelCanvasDocument.WrapForEmbedding(Content, WorkbenchColorScheme.Light);

        Assert.Contains("addEventListener('scroll'", html, StringComparison.Ordinal);
        Assert.Contains("addEventListener('resize'", html, StringComparison.Ordinal);
        Assert.Contains("e.button !== 0", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Exported_document_has_no_csp_so_it_opens_standalone()
    {
        // 导出文件是给用户自己打开/分享的：CSP 是容器约束，带进导出文件会拦住
        // 用户自己加的资源，而且页面上看不出来为什么。
        var html = PanelCanvasDocument.WrapForExport(Content, WorkbenchColorScheme.Light);

        Assert.DoesNotContain("Content-Security-Policy", html);
        Assert.Contains("<title>", html);
    }

    [Fact]
    public void Both_documents_inject_the_host_theme_variables()
    {
        var expected = PanelCanvasTheme.For(WorkbenchColorScheme.Dark).Page;

        foreach (var html in new[]
        {
            PanelCanvasDocument.WrapForEmbedding(Content, WorkbenchColorScheme.Dark),
            PanelCanvasDocument.WrapForExport(Content, WorkbenchColorScheme.Dark),
        })
        {
            Assert.Contains("--pudding-page", html);
            Assert.Contains(expected, html);
        }
    }

    [Fact]
    public void Light_and_dark_wrappers_differ()
    {
        var light = PanelCanvasDocument.WrapForExport(Content, WorkbenchColorScheme.Light);
        var dark = PanelCanvasDocument.WrapForExport(Content, WorkbenchColorScheme.Dark);

        Assert.NotEqual(light, dark);
    }

    [Fact]
    public void Content_is_inserted_verbatim()
    {
        // 内容是 HTML 片段，必须原样插入：一旦做转义，画布上会只剩一行可见的尖括号文本。
        var html = PanelCanvasDocument.WrapForExport(Content, WorkbenchColorScheme.Light);

        Assert.Contains(Content, html);
    }

    [Fact]
    public void Null_content_is_rejected_instead_of_producing_a_blank_canvas()
    {
        Assert.Throws<ArgumentNullException>(
            () => PanelCanvasDocument.WrapForEmbedding(null!, WorkbenchColorScheme.Light));
        Assert.Throws<ArgumentNullException>(
            () => PanelCanvasDocument.WrapForExport(null!, WorkbenchColorScheme.Light));
    }
}
