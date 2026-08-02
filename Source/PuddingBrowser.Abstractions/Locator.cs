namespace PuddingBrowser.Abstractions;

/// <summary>Locator strategy for finding DOM elements.</summary>
public enum LocatorKind
{
    Css,
    XPath,
    Text,
    Role,
    Label,
    Placeholder,
    AltText,
    Title,
    TestId
}

/// <summary>Optional frame selector for cross-frame locators.</summary>
public sealed record FrameSelector
{
    public string? Name { get; init; }
    public string? UrlPattern { get; init; }
}

/// <summary>
/// Describes how to find one or more DOM elements.
/// Supports compound locators via Has/HasText and cross-frame selection.
/// </summary>
public sealed record Locator
{
    public required LocatorKind Kind { get; init; }
    public required string Value { get; init; }
    public string? Name { get; init; }
    public bool Exact { get; init; }
    public int? Nth { get; init; }
    public FrameSelector? Frame { get; init; }
    public Locator? Has { get; init; }
    public string? HasText { get; init; }
}
