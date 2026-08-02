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
