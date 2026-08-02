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

public sealed record ClickOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public int? ClickCount { get; init; }
    public string? Button { get; init; }
}

public sealed record FillOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public bool ClearFirst { get; init; } = true;
}

public sealed record TypeOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public int DelayMs { get; init; }
}

public sealed record KeyOptions
{
    public int TimeoutMs { get; init; } = 5_000;
}

public sealed record PointerOptions
{
    public int TimeoutMs { get; init; } = 5_000;
}

public sealed record ScrollOptions
{
    public int TimeoutMs { get; init; } = 5_000;
    public double? DeltaX { get; init; }
    public double? DeltaY { get; init; }
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
