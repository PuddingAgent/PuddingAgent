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
    public int MaxNodes { get; init; } = 5_000;
    public int MaxTextLength { get; init; } = 200_000;
    public int MaxDepth { get; init; } = 24;
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
}

public sealed record BrowserWaitResultDescriptor
{
    public bool TimedOut { get; init; }
    public string? Error { get; init; }
    public required BrowserPageDescriptor Page { get; init; }
}
