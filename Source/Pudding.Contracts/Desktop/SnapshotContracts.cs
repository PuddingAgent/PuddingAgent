namespace Pudding.Contracts.Desktop;

/// <summary>
/// 快照范围与预算（与既有 Bridge 的 <c>page.snapshot</c> 参数语义等价，计划 §9）。
/// 预算是硬上限：Desktop 必须截断而不是无界返回（截断要如实标注 <c>Truncated</c>）。
/// </summary>
public sealed record DesktopSnapshotOptions
{
    public const int DefaultMaxNodes = 10_000;

    public const int MaxMaxNodes = 50_000;

    public const int DefaultMaxTextLength = 200_000;

    public const int MaxMaxTextLength = 2_000_000;

    public DesktopSnapshotOptions(
        bool includeDom = true,
        bool includeAccessibilityTree = true,
        bool includeHtml = false,
        int maxNodes = DefaultMaxNodes,
        int maxTextLength = DefaultMaxTextLength,
        bool includeHidden = false,
        bool includeIframes = true,
        bool includeShadowDom = true,
        // 设计 §6.1：扫描深度初值 64（**按文档对齐**，round-155；此前为 24 且无记录说明）。
        int maxDepth = 64,
        string? scopeRef = null,
        string? sinceRevision = null,
        bool includeContentBlocks = false)
    {
        if (maxNodes is < 1 or > MaxMaxNodes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxNodes), maxNodes, $"Node budget must be in [1, {MaxMaxNodes}].");
        }

        if (maxTextLength is < 1 or > MaxMaxTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTextLength), maxTextLength, $"Text budget must be in [1, {MaxMaxTextLength}].");
        }

        IncludeDom = includeDom;
        IncludeAccessibilityTree = includeAccessibilityTree;
        IncludeHtml = includeHtml;
        MaxNodes = maxNodes;
        MaxTextLength = maxTextLength;
        IncludeHidden = includeHidden;
        IncludeIframes = includeIframes;
        IncludeShadowDom = includeShadowDom;
        MaxDepth = maxDepth;
        ScopeRef = string.IsNullOrWhiteSpace(scopeRef) ? null : scopeRef.Trim();
        SinceRevision = string.IsNullOrWhiteSpace(sinceRevision) ? null : sinceRevision.Trim();
        IncludeContentBlocks = includeContentBlocks;
    }

    public bool IncludeDom { get; }

    public bool IncludeAccessibilityTree { get; }

    public bool IncludeHtml { get; }

    public int MaxNodes { get; }

    public int MaxTextLength { get; }

    /// <summary>是否包含隐藏元素（加宽 2026-10-02：工具侧一直有这个旋钮）。</summary>
    public bool IncludeHidden { get; }

    /// <summary>是否进入 iframe。</summary>
    public bool IncludeIframes { get; }

    /// <summary>是否进入 shadow DOM。</summary>
    public bool IncludeShadowDom { get; }

    /// <summary>遍历深度上限。</summary>
    public int MaxDepth { get; }

    /// <summary>
    /// 读取**作用域**（设计方案 §151 的 <c>scope</c>）：只从该 ref 的节点（含自己）开始遍历，
    /// 而不是整页。<c>null</c> 表示整页。引用随版本失效，因此过期的 ref 会被驱动明确拒绝
    /// （<c>stale_element_reference</c>），而不是返回一份"看起来是子树其实是整页"的结果。
    /// </summary>
    public string? ScopeRef { get; }

    /// <summary>
    /// 与上次读取的修订标识比较（§145）。**由持有基线的那一侧**（真正读到内容的进程）解释，
    /// 因此这里是原样下传的字符串，而不是已经判定过的结论。
    /// </summary>
    public string? SinceRevision { get; }

    /// <summary>
    /// 是否提取**正文语义块**（设计方案 §6.1 的 <c>mode=text</c>）。驱动只回答「哪块是正文」，
    /// markdown 组装在读取侧（<c>ArticleMarkdown</c>）完成。
    /// </summary>
    public bool IncludeContentBlocks { get; }

    /// <summary>至少需要一种内容，否则快照没有意义（fail closed，不做无内容调用）。</summary>
    public bool HasContent => IncludeDom || IncludeAccessibilityTree || IncludeHtml || IncludeContentBlocks;
}

/// <summary>
/// 快照请求：显式页面目标 + 期望页面版本 + 预算。
/// <see cref="ExpectedPageVersion"/> 为 <see cref="DesktopPageVersion.Unknown"/> 时表示「当前版本即可」；
/// 给出期望值时，Desktop 必须在版本不符时拒绝（<c>page_version_mismatch</c>），不得返回过期快照。
/// </summary>
public sealed record BrowserSnapshotRequest
{
    public BrowserSnapshotRequest(
        DesktopPageTarget target,
        DesktopPageVersion expectedPageVersion = default,
        DesktopSnapshotOptions? options = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ExpectedPageVersion = expectedPageVersion;
        Options = options ?? new DesktopSnapshotOptions();
    }

    public DesktopPageTarget Target { get; }

    public DesktopPageVersion ExpectedPageVersion { get; }

    public DesktopSnapshotOptions Options { get; }
}

/// <summary>
/// 快照结果。<b>Ref 的不变量</b>：元素引用只在同一 <see cref="PageVersion"/> 内有效；
/// 交互提交后不得复用旧 Ref，必须重新取快照或等待状态。
/// </summary>
public sealed record DesktopSnapshot
{
    public DesktopSnapshot(
        DesktopPageTarget target,
        string? domText,
        string? accessibilityTree,
        string? html,
        bool truncated,
        int nodeCount,
        DesktopPageVersion pageVersion,
        string? revision = null,
        bool? changed = null,
        BrowserTextDiff? diff = null,
        string? contentBlocks = null,
        IReadOnlyList<string>? pageEvents = null)
    {
        if (nodeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeCount), nodeCount, "Node count cannot be negative.");
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        DomText = domText;
        AccessibilityTree = accessibilityTree;
        Html = html;
        Truncated = truncated;
        NodeCount = nodeCount;
        PageVersion = pageVersion;
        Revision = revision;
        Changed = changed;
        Diff = diff;
        ContentBlocks = contentBlocks;
        PageEvents = pageEvents;
    }

    public DesktopPageTarget Target { get; }

    public string? DomText { get; }

    public string? AccessibilityTree { get; }

    public string? Html { get; }

    /// <summary>是否因预算或深度限制被截断（如实标注，不假装完整）。</summary>
    public bool Truncated { get; }

    public int NodeCount { get; }

    /// <summary>快照对应的页面版本；拿到 Ref 的调用方必须凭它判断后续操作是否仍然有效。</summary>
    public DesktopPageVersion PageVersion { get; }

    /// <summary>本次读取的修订标识（§145）：回传给下次的 <c>since_revision</c> 即可问"变了什么"。</summary>
    public string? Revision { get; init; }

    /// <summary>
    /// 与调用方给的修订相比内容是否变化；<c>null</c> = 本次没有要求比较
    /// （不可比时整个调用以 <c>refresh_required</c> 失败，而不是在这里猜一个值）。
    /// </summary>
    public bool? Changed { get; init; }

    /// <summary>行级差异（要求比较且基线可用时给出）。</summary>
    public BrowserTextDiff? Diff { get; init; }

    /// <summary>正文语义块（每行一个 JSON 对象）；未请求时为 <c>null</c>。</summary>
    public string? ContentBlocks { get; init; }

    /// <summary>页面自己处理掉的事件事实（有界、无敏感内容）；无事件时为 <c>null</c>。</summary>
    public IReadOnlyList<string>? PageEvents { get; init; }

    public bool IsEmpty => DomText is null && AccessibilityTree is null && Html is null;

    public override string ToString() =>
        $"snapshot @{Target} v{PageVersion.Value} nodes={NodeCount} truncated={Truncated}";
}
