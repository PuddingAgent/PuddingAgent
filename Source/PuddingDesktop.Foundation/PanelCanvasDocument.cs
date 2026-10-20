namespace PuddingDesktop.Foundation;

/// <summary>
/// 画布内容的两种包装：**嵌入宿主**（沙箱）与**导出**（独立文件）。
///
/// 为什么必须分成两种：CSP 是**容器约束**，不是内容的属性。
/// 嵌进宿主时要收紧——内容不该联网，也拿不到任何 native 桥；
/// 但导出成独立文件时若继续带着它，用户在自己机器上打开会被静默拦住自己加的资源，
/// 而页面上不会出现任何提示。
///
/// 纯字符串拼装，因此可以在无窗口环境测；Shell 只负责把结果交给 WebView2 / 写盘。
/// </summary>
public static class PanelCanvasDocument
{
    /// <summary>
    /// 嵌入宿主时的 CSP：断网、禁一切外部资源，只放开内联样式/脚本（画布交互靠内联 JS）
    /// 与 <c>data:</c> 图片、字体。
    /// </summary>
    public const string EmbeddingContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; "
        + "img-src data:; font-src data:;";

    /// <summary>
    /// CSS 兜底：内容只写 <c>var(--pudding-*)</c> 即可，不需要自己猜主题。
    /// 变量缺失时（例如样式表没注入）仍有 <c>--pudding-page</c> 的 fallback。
    /// </summary>
    private const string BaseCss =
        "html,body{margin:0;background:var(--pudding-page);"
        + "font-family:'Segoe UI','Microsoft YaHei',sans-serif;}";

    /// <summary>
    /// 包成嵌入宿主的文档：带沙箱 CSP 与宿主主题变量。
    /// <paramref name="content"/> 是**片段**（<c>&lt;style&gt;</c>/<c>&lt;div&gt;</c>/<c>&lt;svg&gt;</c>…），
    /// 原样插入 body，**不做 HTML 转义** —— 它本来就是 HTML。
    /// </summary>
    public static string WrapForEmbedding(string content, WorkbenchColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Header(PanelCanvasTheme.Declare(scheme), title: null, csp: EmbeddingContentSecurityPolicy)
            + content + "</body></html>";
    }

    /// <summary>
    /// 包成可独立打开的文档：**不带 CSP**（那是容器约束），但保留主题变量，
    /// 这样在浏览器里打开时配色仍然正确。
    /// </summary>
    public static string WrapForExport(string content, WorkbenchColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Header(PanelCanvasTheme.Declare(scheme), title: "Pudding 画布", csp: null)
            + content + "</body></html>";
    }

    private static string Header(string themeCss, string? title, string? csp)
    {
        var head = "<!doctype html><html><head><meta charset=\"utf-8\">";
        if (csp is not null)
        {
            head += "<meta http-equiv=\"Content-Security-Policy\" content=\"" + csp + "\">";
        }

        if (title is not null)
        {
            head += "<title>" + title + "</title>";
        }

        return head + "<style>" + themeCss + BaseCss + "</style></head><body>";
    }
}
