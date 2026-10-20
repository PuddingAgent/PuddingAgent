namespace Pudding.Contracts.Desktop;

/// <summary>文档预览的阶段。accepted 只表示 Desktop 已受理，<b>不表示内容已经渲染</b>。</summary>
public enum DesktopDocumentPreviewStatus
{
    /// <summary>已受理并开始准备内容；调用方随后应通过工具区观察结果。</summary>
    Accepted,

    /// <summary>内容已就绪并已呈现。</summary>
    Opened,

    /// <summary>同一路径的标签已存在，本次请求只是把它激活（幂等）。</summary>
    AlreadyOpen,
}

/// <summary>
/// 文档预览请求：<b>只带路径</b>。
///
/// 边界（为什么这么窄）：Core 只负责表达「请给我看这个文件」，读盘、解码、渲染、排版
/// 全部属于 Desktop 自有组件。因此这里没有正文、没有 HTML，也不接受绝对路径——
/// 正文会让线缆与审计带上用户内容，绝对路径会把 Core 的磁盘布局泄给 Desktop。
///
/// 注意：这里只做<b>形状</b>校验（非空 / 长度 / 控制字符）。真正的路径语义策略
/// （盘符、UNC、traversal、扩展名白名单、根目录前缀复核）刻意不放在契约层，
/// 因为它是各层自己的边界：Core 发送前用 <c>DocumentPathPolicy</c> 拦一次，
/// Desktop 收到后再用自己的策略拦一次。契约层替任何一方下语义判断，
/// 都会让另一方的 fail-closed 变成空话。
/// </summary>
public sealed record DesktopDocumentPreviewRequest
{
    public const int MaxPathLength = 512;

    public const int MaxTitleLength = 128;

    public DesktopDocumentPreviewRequest(string path, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be non-empty.", nameof(path));
        }

        if (path.Length > MaxPathLength)
        {
            throw new ArgumentException($"Path must be at most {MaxPathLength} characters.", nameof(path));
        }

        if (path.Any(char.IsControl))
        {
            throw new ArgumentException("Path must not contain control characters.", nameof(path));
        }

        Path = path;
        Title = string.IsNullOrWhiteSpace(title)
            ? null
            : ContractText.RequireDisplayText(title, MaxTitleLength, nameof(title));
    }

    /// <summary>工作区相对路径；分隔符与大小写归一是使用方的事，不是契约的事。</summary>
    public string Path { get; }

    /// <summary>可选显示标题；为空时由 Desktop 用文件名。</summary>
    public string? Title { get; }

    /// <summary>只给形状：路径可能含用户目录名，不进日志与审计。</summary>
    public override string ToString() =>
        $"document_preview(pathChars={Path.Length}, hasTitle={Title is not null})";
}

/// <summary>
/// 文档预览结果：只回身份与阶段，<b>不回正文</b>。
/// <see cref="TabId"/> 是 Desktop 侧工具区的标签身份，供调用方对齐「是不是同一个视图」。
/// </summary>
public sealed record DesktopDocumentPreviewResult
{
    public DesktopDocumentPreviewResult(string tabId, string path, DesktopDocumentPreviewStatus status)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            throw new ArgumentException("Tab id must be non-empty.", nameof(tabId));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be non-empty.", nameof(path));
        }

        TabId = tabId;
        Path = path;
        Status = status;
    }

    public string TabId { get; }

    /// <summary>被预览的工作区相对路径（原样回显，便于调用方对齐；不含绝对路径）。</summary>
    public string Path { get; }

    public DesktopDocumentPreviewStatus Status { get; }

    public override string ToString() => $"document_preview({Status}, tabChars={TabId.Length})";
}
