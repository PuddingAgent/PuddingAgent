using System.Text.Json;

namespace PuddingBrowser.Abstractions;

/// <summary>JavaScript to execute in a browser page.</summary>
public sealed record BrowserScript
{
    public required string Source { get; init; }
    public JsonElement? Argument { get; init; }
    public bool AwaitPromise { get; init; } = true;
    public bool ReturnByValue { get; init; } = true;
}

/// <summary>Result of a JavaScript evaluation.</summary>
public sealed record BrowserScriptValue
{
    public string? Type { get; init; }
    public string? Subtype { get; init; }
    public JsonElement? Value { get; init; }
    public string? Description { get; init; }
    public JsHandleId? HandleId { get; init; }
}

public sealed record SnapshotOptions
{
    public bool IncludeDom { get; init; } = true;
    public bool IncludeAccessibilityTree { get; init; } = true;
    public bool IncludeHidden { get; init; }
    public bool IncludeIframes { get; init; } = true;
    public bool IncludeShadowDom { get; init; } = true;
    public bool IncludeHtml { get; init; }
    public int MaxNodes { get; init; } = 5_000;
    public int MaxTextLength { get; init; } = 200_000;
}

public sealed record PageSnapshot
{
    public string? DomText { get; init; }
    public string? AccessibilityTree { get; init; }
    public string? Html { get; init; }
    public bool Truncated { get; init; }
    public int NodeCount { get; init; }
}

public sealed record BoundingBox
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed record BrowserCookie
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string? Domain { get; init; }
    public string? Path { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public bool HttpOnly { get; init; }
    public bool Secure { get; init; }
    public string? SameSite { get; init; }
}
