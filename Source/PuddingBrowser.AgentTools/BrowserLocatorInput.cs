using PuddingBrowser.Abstractions;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserLocatorInput
{
    [ToolParam("Locator kind: ref, css, xpath, text, role, label, placeholder, alt_text, title, or test_id.")]
    public required string Kind { get; init; }

    [ToolParam("Locator value. For ref, use a ref from browser_snapshot or browser_locate.")]
    public required string Value { get; init; }

    [ToolParam("Optional accessible name used with role locators.")]
    public string? Name { get; init; }

    [ToolParam("Require an exact text/name match.")]
    public bool Exact { get; init; }

    [ToolParam("Optional zero-based result index.")]
    public int? Nth { get; init; }

    [ToolParam("Optional text that the matched element must contain.")]
    public string? HasText { get; init; }
}

internal static class BrowserLocatorInputMapper
{
    public static Locator ToLocator(BrowserLocatorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = input.Kind?.Trim().Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(input.Value)
            || !Enum.TryParse<LocatorKind>(normalized, ignoreCase: true, out var kind))
        {
            throw new BrowserOperationException(
                "browser_invalid_arguments",
                "locator.kind/value must identify ref, css, xpath, text, role, label, placeholder, alt_text, title, or test_id");
        }
        if (input.Nth is < 0)
            throw new BrowserOperationException("browser_invalid_arguments", "locator.nth must be zero or greater");
        return new Locator
        {
            Kind = kind,
            Value = input.Value,
            Name = input.Name,
            Exact = input.Exact,
            Nth = input.Nth,
            HasText = input.HasText
        };
    }
}
