namespace Pudding.Contracts.Desktop;

/// <summary>面板渲染的阶段。<see cref="Rendered"/> 只表示内容已交给画布，<b>不表示用户已经看到</b>。</summary>
public enum DesktopPanelRenderStatus
{
    /// <summary>新建了画布并渲染了内容。</summary>
    Rendered,

    /// <summary>画布已存在，本次请求替换了其中的内容。</summary>
    Replaced,
}

/// <summary>
/// 面板渲染请求：把 Agent 生成的一段<b>自包含 HTML</b> 推进工具区画布。
///
/// 边界（为什么内容走线缆而不是落盘再传路径）：面板展示的是「现算现给」的媒体资源
/// （结构图、表格、可交互小部件），没有天然的文件生命周期；为它造临时文件只会多出
/// 命名与清理问题。安全性不靠「不传内容」，而靠实现方把它装进<b>无桥接、无网络</b>
/// 的沙箱文档渲染 —— 也就是说，这里允许内容上线缆，但不允许它获得任何特权。
///
/// 注意：契约层只做<b>形状</b>校验（非空 / 长度 / 标识字符集 / 内容类型白名单）。
/// 「这段 HTML 安不安全」不是形状问题，无法在这里判定；实现方必须用 CSP 与禁用
/// 桥接来兜底，而不是指望上游消毒。
/// </summary>
public sealed record DesktopPanelRenderRequest
{
    public const int MaxPanelIdLength = 64;

    public const int MaxTitleLength = 128;

    /// <summary>
    /// 内容上限 256 KB（字符）。取这个量级的原因：面板是<b>人看的画布</b>，
    /// 不是数据传输通道；超过这个体积通常意味着该改成文档预览而不是面板渲染。
    /// </summary>
    public const int MaxContentLength = 256 * 1024;

    /// <summary>v1 唯一接受的内容类型。</summary>
    public const string HtmlContentType = "html";

    public DesktopPanelRenderRequest(
        string panelId,
        string content,
        string contentType = HtmlContentType,
        string? title = null)
    {
        if (string.IsNullOrWhiteSpace(panelId))
        {
            throw new ArgumentException("Panel id must be non-empty.", nameof(panelId));
        }

        if (panelId.Length > MaxPanelIdLength)
        {
            throw new ArgumentException(
                $"Panel id must be at most {MaxPanelIdLength} characters.", nameof(panelId));
        }

        if (!IsValidPanelId(panelId))
        {
            throw new ArgumentException(
                "Panel id may only contain letters, digits, '.', '_' and '-'.", nameof(panelId));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Content must be non-empty.", nameof(content));
        }

        if (content.Length > MaxContentLength)
        {
            throw new ArgumentException(
                $"Content must be at most {MaxContentLength} characters.", nameof(content));
        }

        // 内容类型必须显式且在白名单内：猜着渲染的表现是用户看到一片空白，
        // 而且没人分得清「内容错了」还是「渲染错了」。
        if (!string.Equals(contentType, HtmlContentType, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Content type '{contentType}' is not supported; v1 accepts only '{HtmlContentType}'.",
                nameof(contentType));
        }

        PanelId = panelId;
        Content = content;
        ContentType = contentType;
        Title = string.IsNullOrWhiteSpace(title)
            ? null
            : ContractText.RequireDisplayText(title, MaxTitleLength, nameof(title));
    }

    /// <summary>稳定画布标识：同 id 覆盖同一画布，不同 id 各自独立。</summary>
    public string PanelId { get; }

    /// <summary>自包含 HTML。实现方必须按不可信内容处理。</summary>
    public string Content { get; }

    public string ContentType { get; }

    /// <summary>可选画布标题；为空时由实现方取默认标题。</summary>
    public string? Title { get; }

    /// <summary>只给形状：<b>绝不回显内容</b>（内容可能含用户数据，会进日志与审计）。</summary>
    public override string ToString() =>
        $"panel_render(panelChars={PanelId.Length}, contentChars={Content.Length}, hasTitle={Title is not null})";

    private static bool IsValidPanelId(string panelId)
    {
        foreach (var ch in panelId)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 面板渲染结果：只回画布身份与阶段，<b>不回内容</b>。
/// 没有内容回显是刻意的 —— 内容既不上回调，也不进审计。
/// </summary>
public sealed record DesktopPanelRenderResult
{
    public DesktopPanelRenderResult(string panelId, DesktopPanelRenderStatus status)
    {
        if (string.IsNullOrWhiteSpace(panelId))
        {
            throw new ArgumentException("Panel id must be non-empty.", nameof(panelId));
        }

        PanelId = panelId;
        Status = status;
    }

    public string PanelId { get; }

    public DesktopPanelRenderStatus Status { get; }

    public override string ToString() => $"panel_render({Status}, panelChars={PanelId.Length})";
}

/// <summary>
/// 面板渲染状态的线名真源（rendered / replaced）。
/// 未知线名折叠为 <see cref="DesktopPanelRenderStatus.Rendered"/>：
/// 这比「当作已替换」保守 —— 调用方据此知道内容已交付，但不必假设是哪一种路径。
/// </summary>
public static class DesktopPanelRenderStatusWire
{
    public static string NameOf(DesktopPanelRenderStatus status) => status switch
    {
        DesktopPanelRenderStatus.Replaced => "replaced",
        _ => "rendered",
    };

    public static DesktopPanelRenderStatus Parse(string? name) => name switch
    {
        "replaced" => DesktopPanelRenderStatus.Replaced,
        _ => DesktopPanelRenderStatus.Rendered,
    };
}
