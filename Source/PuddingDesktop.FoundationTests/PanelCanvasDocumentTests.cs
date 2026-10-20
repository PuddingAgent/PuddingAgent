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
