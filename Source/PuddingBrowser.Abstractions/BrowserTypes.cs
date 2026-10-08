using System.Text.Json;

namespace PuddingBrowser.Abstractions;

/// <summary>Browser runtime state.</summary>
public enum BrowserRuntimeState
{
    Created,
    Starting,
    Ready,
    ShuttingDown,
    Disposed
}

public sealed record BrowserContextInfo
{
    public required BrowserContextId Id { get; init; }
    public required string UserDataDirectory { get; init; }
    public bool Persistent { get; init; }
    public int PageCount { get; init; }
}

public sealed record PageInfo
{
    public required PageId Id { get; init; }
    public required BrowserContextId ContextId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public long PageVersion { get; init; }
}

public sealed record NavigationOptions
{
    public int TimeoutMs { get; init; } = 30_000;
    public string? Referer { get; init; }
}

public sealed record NavigationResult
{
    public required Uri Url { get; init; }
    public bool Ok { get; init; }
    public int? StatusCode { get; init; }
    public string? ErrorText { get; init; }
}

/// <summary>
/// 输入路径（设计方案 §7）：<c>Dom</c> = DOM 事件/属性写入（语义确定、已验证适用）；
/// <c>Native</c> = 真实浏览器输入（CDP 键鼠）。**不可用时必须明确 unsupported，不静默回落**。
/// </summary>
public enum InputMode
{
    Dom,
    Native,
}
public sealed record ClickOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public int? ClickCount { get; init; }
    public string? Button { get; init; }
    public InputMode InputMode { get; init; }
}

public sealed record FillOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public InputMode InputMode { get; init; }
    public bool ClearFirst { get; init; } = true;
}

public sealed record TypeOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public InputMode InputMode { get; init; }
    public int DelayMs { get; init; }
}

public sealed record KeyOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public InputMode InputMode { get; init; }
}

public sealed record PointerOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public InputMode InputMode { get; init; }
}

/// <summary>
/// 滚动目标模型（设计方案 §7）：<c>page/element/to_ref/to_top/to_bottom</c>。
/// 「无 locator 时检查 <c>document.scrollingElement</c>；元素滚动优先明确指定，
/// 必要时定位最近可滚动祖先」——**不盲目滚另一个区域**。
/// </summary>
public enum ScrollTarget
{
    /// <summary>滚动文档本身（无 locator）。</summary>
    Page,

    /// <summary>滚动**指定元素自身**的滚动盒（需要 locator）。</summary>
    Element,

    /// <summary>把指定元素滚入可见区域（需要 locator）。</summary>
    ToRef,

    /// <summary>滚到顶部：无 locator 时是文档，有 locator 时是它最近的可滚动祖先。</summary>
    ToTop,

    /// <summary>滚到底部：无 locator 时是文档，有 locator 时是它最近的可滚动祖先。</summary>
    ToBottom,
}

/// <summary>
/// 滚动目标 ↔ 线名（**唯一真源**）：驱动的滚动脚本与桥接命令都从这里取字面量。
/// 之前 Core、Desktop、驱动各写一份 switch，加/改目标时必须同时改三处——那正是漂移的来源。
/// 与契约层的 <c>DesktopScrollTargetWire</c> 必须逐项一致，由跨层契约测试钉住。
/// </summary>
public static class ScrollTargetWire
{
    public static string NameOf(ScrollTarget target) => target switch
    {
        ScrollTarget.Page => "page",
        ScrollTarget.Element => "element",
        ScrollTarget.ToRef => "to_ref",
        ScrollTarget.ToTop => "to_top",
        ScrollTarget.ToBottom => "to_bottom",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Scroll target is not registered."),
    };

    /// <summary>严格解析：不知道的线名不猜测、不回退（fail closed）。</summary>
    public static bool TryParse(string? name, out ScrollTarget target)
    {
        foreach (var candidate in Enum.GetValues<ScrollTarget>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                target = candidate;
                return true;
            }
        }

        target = default;
        return false;
    }
}

public sealed record ScrollOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public InputMode InputMode { get; init; }
    public double? DeltaX { get; init; }
    public double? DeltaY { get; init; }

    /// <summary>目标模型；默认滚文档（与迁移前一致）。</summary>
    public ScrollTarget Target { get; init; } = ScrollTarget.Page;

    /// <summary><see cref="ScrollTarget.Element"/> / <see cref="ScrollTarget.ToRef"/> 需要的元素定位。</summary>
    public Locator? Locator { get; init; }
}

/// <summary>
/// 一次滚动的**结果事实**（设计方案 §7）：目标容器、前后位置、实际消耗的位移、是否到边界、
/// 是否观察到内容变化。
///
/// 「无限滚动另等新增内容/条目数，**不能把无内容加载当滚动失败**」⇒
/// <see cref="ContentChanged"/> 为 <c>false</c> **不是失败**；调用方可据
/// <see cref="ConsumedDeltaY"/>/<see cref="AtBoundary"/> 判断"是不是滚不动了"。
/// </summary>
public sealed record ScrollOutcome
{
    /// <summary>实际滚动的容器：<c>document</c> / <c>element</c> / <c>scrollable-ancestor</c>。</summary>
    public required string Container { get; init; }

    public double BeforeX { get; init; }
    public double BeforeY { get; init; }
    public double AfterX { get; init; }
    public double AfterY { get; init; }

    /// <summary>实际消耗的位移（请求值 - 未生效部分）；为 0 表示"滚不动"。</summary>
    public double ConsumedDeltaX { get; init; }
    public double ConsumedDeltaY { get; init; }

    /// <summary>是否已到边界（滚不动且方向一致）。</summary>
    public bool AtBoundary { get; init; }

    /// <summary>滚动前后是否观察到内容变化（新增内容/条目）。<b>false 不是失败</b>。</summary>
    public bool ContentChanged { get; init; }
}

public sealed record DragOptions
{
    public int TimeoutMs { get; init; } = 5_000;
}

public sealed record ScreenshotOptions
{
    public string? FilePath { get; init; }
    public bool FullPage { get; init; }
    public int? Quality { get; init; }

    /// <summary>
    /// 截图的字节上限。**超了不截断图片**（半张图会让读者得出错误结论），而是明确报资源耗尽。
    /// </summary>
    public int? MaxBytes { get; init; }
}

public sealed record ScreenshotResult
{
    public byte[]? Bytes { get; init; }
    public string? FilePath { get; init; }
}

public sealed record PdfOptions
{
    public string? FilePath { get; init; }
    public string? Format { get; init; }
    public bool PrintBackground { get; init; }
}

public sealed record PdfResult
{
    public byte[]? Bytes { get; init; }
    public string? FilePath { get; init; }
}

public sealed record WaitCondition
{
    public string? Selector { get; init; }
    public string? SelectorToHide { get; init; }
    public string? UrlPattern { get; init; }
    public int TimeoutMs { get; init; } = 30_000;
}

public sealed record WaitResult
{
    public bool TimedOut { get; init; }
    public string? Error { get; init; }
}

public sealed class StaleBrowserHandleException : InvalidOperationException
{
    public StaleBrowserHandleException(PageId pageId, long expected, long actual)
        : base($"Stale handle: page {pageId.Value} version {expected}, current {actual}") { }
}

/// <summary>
/// Stable browser-domain failure propagated across an out-of-process browser runtime.
/// Tool layers should surface <see cref="Code"/> instead of parsing exception text.
/// </summary>
public sealed class BrowserOperationException : InvalidOperationException
{
    public BrowserOperationException(string code, string message)
        : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "browser_operation_failed" : code;
    }

    public string Code { get; }
}

// ── Browser Event Model ──────────────────────────────────

public abstract record BrowserEvent(
    DateTimeOffset Timestamp,
    BrowserContextId ContextId,
    PageId? PageId,
    long? PageVersion);

public sealed record BrowserNavigationEvent(
    DateTimeOffset Timestamp, BrowserContextId ContextId, PageId? PageId, long? PageVersion,
    Uri Url, bool IsMainFrame) : BrowserEvent(Timestamp, ContextId, PageId, PageVersion);

public sealed record BrowserConsoleEvent(
    DateTimeOffset Timestamp, BrowserContextId ContextId, PageId? PageId, long? PageVersion,
    string Level, string Text) : BrowserEvent(Timestamp, ContextId, PageId, PageVersion);

public sealed record BrowserPageErrorEvent(
    DateTimeOffset Timestamp, BrowserContextId ContextId, PageId? PageId, long? PageVersion,
    string Message, string? Stack) : BrowserEvent(Timestamp, ContextId, PageId, PageVersion);

public sealed record BrowserDialogEvent(
    DateTimeOffset Timestamp, BrowserContextId ContextId, PageId? PageId, long? PageVersion,
    string DialogType, string Message) : BrowserEvent(Timestamp, ContextId, PageId, PageVersion);

public sealed record BrowserDownloadEvent(
    DateTimeOffset Timestamp, BrowserContextId ContextId, PageId? PageId, long? PageVersion,
    DownloadId DownloadId, string Url, string? SuggestedFilename, string? State)
    : BrowserEvent(Timestamp, ContextId, PageId, PageVersion);

public sealed record BrowserNewPageEvent(
    DateTimeOffset Timestamp, BrowserContextId ContextId, PageId? PageId, long? PageVersion,
    PageId NewPageId, Uri? Url) : BrowserEvent(Timestamp, ContextId, PageId, PageVersion);

public sealed record BrowserProcessFailedEvent(
    DateTimeOffset Timestamp, BrowserContextId ContextId, PageId? PageId, long? PageVersion,
    string ProcessKind, int ExitCode) : BrowserEvent(Timestamp, ContextId, PageId, PageVersion);

public sealed record BrowserCdpEvent(
    DateTimeOffset Timestamp, BrowserContextId ContextId, PageId? PageId, long? PageVersion,
    string EventName, JsonDocument Payload) : BrowserEvent(Timestamp, ContextId, PageId, PageVersion)
{
    public override string ToString() => $"{EventName}@{PageId}";
}

public sealed record BrowserEventFilter
{
    public BrowserContextId? ContextId { get; init; }
    public PageId? PageId { get; init; }
    public IReadOnlySet<string>? EventTypes { get; init; }
}
