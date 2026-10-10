namespace PuddingBrowser.Protocol;

/// <summary>
/// Strongly-typed command argument and result DTOs for the Browser Bridge protocol.
/// These replace scattered JsonElement.GetProperty(...) calls in Controller/Dispatcher.
/// </summary>

// ─── Command Arguments ───────────────────────────────────────────────────────

public sealed record ContextCreateArguments
{
    public string? ContextId { get; init; }
}

public sealed record ContextCloseArguments
{
    public string? ContextId { get; init; }
}

public sealed record ContextGetInfoArguments
{
    public string? ContextId { get; init; }
}

public sealed record PageCreateArguments
{
    public string? InitialUrl { get; init; }
    public bool Activate { get; init; } = true;
}

public sealed record PageGotoArguments
{
    public required string Url { get; init; }
    public int TimeoutMs { get; init; } = 30_000;
}

public sealed record PageActivateArguments
{
    public required string PageId { get; init; }
}

public sealed record PageCloseArguments
{
    public required string PageId { get; init; }
}

public sealed record BrowserLocatorDescriptor
{
    public required string Kind { get; init; }
    public required string Value { get; init; }
    public string? Name { get; init; }
    public bool Exact { get; init; }
    public int? Nth { get; init; }
    public string? HasText { get; init; }
}

public sealed record PageSnapshotArguments
{
    public bool IncludeDom { get; init; } = true;
    public bool IncludeAccessibilityTree { get; init; } = true;
    public bool IncludeHidden { get; init; }
    public bool IncludeIframes { get; init; } = true;
    public bool IncludeShadowDom { get; init; } = true;
    public bool IncludeHtml { get; init; }
    public int MaxNodes { get; init; } = 10_000;
    public int MaxTextLength { get; init; } = 200_000;
    public int MaxDepth { get; init; } = 64;

    /// <summary>
    /// 读取作用域（设计方案 §151 的 <c>scope</c>）：只从该 ref 的节点（含自己）开始遍历；
    /// <c>null</c> = 整页。引用过期由宿主明确拒绝，**不退化成正页**。
    /// </summary>
    public string? ScopeRef { get; init; }

    /// <summary>与上次读取的修订标识比较（§145）；由**持有基线的宿主侧**解释。</summary>
    public string? SinceRevision { get; init; }

    /// <summary>是否提取正文语义块（§6.1 的 <c>mode=text</c>）。</summary>
    public bool IncludeContentBlocks { get; init; }
}

public sealed record PageLocateArguments
{
    public required BrowserLocatorDescriptor Locator { get; init; }
}

public sealed record PageInteractArguments
{
    public required string Action { get; init; }
    public BrowserLocatorDescriptor? Locator { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<string>? Values { get; init; }
    public bool? Checked { get; init; }
    public double? DeltaX { get; init; }
    public double? DeltaY { get; init; }

    /// <summary>
    /// 滚动目标模型（设计方案 §7）：<c>page|element|to_ref|to_top|to_bottom</c>。
    /// 省略时宿主按 locator/位移推导（与能力通道路径同一套规则）。
    /// </summary>
    public string? ScrollTarget { get; init; }
    /// <summary>§8.2 坐标输入的 X（**CSS viewport 像素**）；仅 pointer 动作使用。</summary>
    public double? X { get; init; }

    /// <summary>§8.2 坐标输入的 Y（**CSS viewport 像素**）。</summary>
    public double? Y { get; init; }

    /// <summary>截图来源坐标的 observationId；无观察登记处可校验时消费端必须**明确失败**。</summary>
    public string? ObservationId { get; init; }
}

public sealed record PageWaitForArguments
{
    public string? Selector { get; init; }
    public string? SelectorToHide { get; init; }
    public string? UrlPattern { get; init; }
    public int TimeoutMs { get; init; } = 30_000;
}

// ─── Result Descriptors ──────────────────────────────────────────────────────

public sealed record BrowserContextDescriptor
{
    public required string ContextId { get; init; }
    public required string UserDataDirectory { get; init; }
    public required int PageCount { get; init; }
}

public sealed record BrowserPageDescriptor
{
    public required string ContextId { get; init; }
    public required string PageId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required long PageVersion { get; init; }
    public bool IsActive { get; init; }
    public bool IsAgentTarget { get; init; }
    public bool CanGoBack { get; init; }
    public bool CanGoForward { get; init; }
    public bool IsLoading { get; init; }
}

public sealed record BrowserNavigationResultDescriptor
{
    public required string Url { get; init; }
    public required bool Ok { get; init; }
    public int? StatusCode { get; init; }
    public string? ErrorText { get; init; }
    public required BrowserPageDescriptor Page { get; init; }
}

public sealed record BrowserPageListDescriptor
{
    public required IReadOnlyList<BrowserPageDescriptor> Pages { get; init; }
}

public sealed record BrowserContextListDescriptor
{
    public required IReadOnlyList<BrowserContextDescriptor> Contexts { get; init; }
}

public sealed record BrowserSnapshotDescriptor
{
    public string? DomText { get; init; }
    public string? AccessibilityTree { get; init; }
    public string? Html { get; init; }
    public bool Truncated { get; init; }
    public int NodeCount { get; init; }
    public long PageVersion { get; init; }

    /// <summary>本次读取的修订标识（§145）；回传给下次的 <c>sinceRevision</c>。</summary>
    public string? Revision { get; init; }

    /// <summary>正文语义块（每行一个 JSON 对象）；markdown 由读取侧组装。</summary>
    public string? ContentBlocks { get; init; }

    /// <summary>页面自己处理掉的事件事实（脚本对话框/权限请求/新窗口/下载；有界、无敏感内容）。</summary>
    public IReadOnlyList<string>? PageEvents { get; init; }

    /// <summary>与调用方给的修订相比内容是否变化；未要求比较时为 <c>null</c>。</summary>
    public bool? Changed { get; init; }

    /// <summary>行级差异（要求比较且基线可用时给出）。计数是**真实**的，列表可能因预算被截断。</summary>
    public BrowserTextDiffDescriptor? Diff { get; init; }
}

/// <summary>行级差异的线缆形态（§145）：「变化了什么」而不是"变了/没变」。</summary>
public sealed record BrowserTextDiffDescriptor
{
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
    public int AddedCount { get; init; }
    public int RemovedCount { get; init; }
    public bool Truncated { get; init; }
}

public sealed record BrowserBoundingBoxDescriptor
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed record BrowserElementDescriptor
{
    public required string Ref { get; init; }
    public required string Tag { get; init; }
    public string? Role { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public bool Visible { get; init; }
    public bool Enabled { get; init; }
    public bool? Checked { get; init; }
    public BrowserBoundingBoxDescriptor? BoundingBox { get; init; }
    public long PageVersion { get; init; }
}

public sealed record BrowserLocateResultDescriptor
{
    public required IReadOnlyList<BrowserElementDescriptor> Elements { get; init; }
    public bool Truncated { get; init; }
}

public sealed record BrowserInteractionResultDescriptor
{
    public BrowserElementDescriptor? Element { get; init; }
    public required BrowserPageDescriptor Page { get; init; }

    /// <summary>
    /// 滚动结果事实（设计方案 §7）。非滚动动作或宿主未报告时为 <c>null</c> ——
    /// 消费方据此知道"没有事实"，而不是"滚了 0 像素"。
    /// </summary>
    public BrowserScrollReportDescriptor? Scroll { get; init; }
}

/// <summary>
/// 滚动结果事实的线缆形态（设计方案 §7）：目标容器、前后位置、消耗位移、是否到边界、内容是否变化。
/// 「无内容加载不是失败」由消费方判读，不在线缆上编造。
/// </summary>
public sealed record BrowserScrollReportDescriptor
{
    public required string Container { get; init; }
    public double BeforeX { get; init; }
    public double BeforeY { get; init; }
    public double AfterX { get; init; }
    public double AfterY { get; init; }
    public double ConsumedDeltaX { get; init; }
    public double ConsumedDeltaY { get; init; }
    public bool AtBoundary { get; init; }
    public bool ContentChanged { get; init; }
}

public sealed record BrowserWaitResultDescriptor
{
    public bool TimedOut { get; init; }
    public string? Error { get; init; }
    public required BrowserPageDescriptor Page { get; init; }
}
