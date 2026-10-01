namespace Pudding.Contracts.Desktop;

/// <summary>
/// 快照范围与预算（与既有 Bridge 的 <c>page.snapshot</c> 参数语义等价，计划 §9）。
/// 预算是硬上限：Desktop 必须截断而不是无界返回（截断要如实标注 <c>Truncated</c>）。
/// </summary>
public sealed record DesktopSnapshotOptions
{
    public const int DefaultMaxNodes = 5_000;

    public const int MaxMaxNodes = 50_000;

    public const int DefaultMaxTextLength = 200_000;

    public const int MaxMaxTextLength = 2_000_000;

    public DesktopSnapshotOptions(
        bool includeDom = true,
        bool includeAccessibilityTree = true,
        bool includeHtml = false,
        int maxNodes = DefaultMaxNodes,
        int maxTextLength = DefaultMaxTextLength)
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
    }

    public bool IncludeDom { get; }

    public bool IncludeAccessibilityTree { get; }

    public bool IncludeHtml { get; }

    public int MaxNodes { get; }

    public int MaxTextLength { get; }

    /// <summary>至少需要一种内容，否则快照没有意义（fail closed，不做无内容调用）。</summary>
    public bool HasContent => IncludeDom || IncludeAccessibilityTree || IncludeHtml;
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
        DesktopPageVersion pageVersion)
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

    public bool IsEmpty => DomText is null && AccessibilityTree is null && Html is null;

    public override string ToString() =>
        $"snapshot @{Target} v{PageVersion.Value} nodes={NodeCount} truncated={Truncated}";
}
