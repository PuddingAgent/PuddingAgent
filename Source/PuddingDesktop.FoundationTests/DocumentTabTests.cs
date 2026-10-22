namespace PuddingDesktop.FoundationTests;

using PuddingDesktop.Foundation;

/// <summary>
/// 「文档」标签页（ToolTabKind.Document）的纯逻辑契约。
/// <para>
/// 这一族是用户 2026-10-20 明确要求的：文档预览搬进 Shell 工具区，用 WebView2 加载
/// /doc-preview，不再占用对话区的横向宽度。UI 那半边无法在无窗口环境验证，但
/// 「同文档只开一个 tab」「可用性标签」「可关闭」三条判据都在 Foundation 里，
/// 所以它们必须在这里被钉住 —— 否则 UI 接好了、身份策略错了，表现是
/// 「点两次文档出现两个 tab」，而没有任何断言会失败。
/// </para>
/// </summary>
public sealed class DocumentTabTests
{
    [Fact]
    public void Document_is_ready_because_webview2_is_a_present_capability()
    {
        // 与 Terminal/Artifact/Panel 不同：文档预览不需要新的进程或后端能力，
        // WebView2 宿主已经存在，所以它不该被标成「待接入」。
        Assert.Equal(ToolTabAvailability.Ready, ToolAvailabilityCatalog.For(ToolTabKind.Document));
        Assert.Equal("可用", ToolAvailabilityCatalog.CardLabel(ToolTabKind.Document));
    }

    [Fact]
    public void Document_identity_folds_separator_and_casing()
    {
        var forward = ToolTabIdentity.Document("Docs/14_reports/A.md");
        var backward = ToolTabIdentity.Document("Docs\\14_reports\\a.md");

        // Windows 路径大小写不敏感、两种分隔符等价：同一份文档必须折叠成一个 tab。
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Different_documents_do_not_collapse_into_one_tab()
    {
        // 上一条的对照组：折叠逻辑不能宽到把不同文件也合并 —— 那会让人以为
        // 自己打开了 B，看到的却是 A。
        Assert.NotEqual(
            ToolTabIdentity.Document("Docs/a.md"),
            ToolTabIdentity.Document("Docs/b.md"));
    }

    [Fact]
    public void Reopening_the_same_document_keeps_one_tab_and_it_can_close()
    {
        var tabs = new ToolWorkspaceTabs();

        var first = tabs.Open(Descriptor("Docs/a.md"));
        var second = tabs.Open(Descriptor("docs\\A.MD"));

        Assert.Single(tabs.Tabs);
        Assert.Equal(first.Id, second.Id);
        Assert.Same(second, tabs.ActiveTab);
        Assert.True(second.CanClose);
    }

    private static ToolTabDescriptor Descriptor(string path) => new()
    {
        Id = ToolTabIdentity.Document(path),
        Kind = ToolTabKind.Document,
        Title = "a.md",
        ResourceKey = path,
    };
}
