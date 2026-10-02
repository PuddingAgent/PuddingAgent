using Pudding.Contracts.Desktop;
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
    /// <summary>
    /// 工具入参 → **能力形状**的定位描述符（切片 D：改走窄端口的工具用这个）。
    /// kind 的归一化与校验与 <see cref="ToLocator"/> 相同（两套定位枚举成员名一一对应）。
    /// 过渡期两份并存：等七个工具都迁移完，<c>ToLocator</c> 与其运行时枚举即可删除。
    /// </summary>
    public static DesktopLocator ToDesktopLocator(BrowserLocatorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var runtimeKind = ParseKind(input);
        if (!Enum.TryParse<DesktopLocatorKind>(runtimeKind.ToString(), ignoreCase: false, out var kind))
        {
            throw new BrowserOperationException(
                "browser_invalid_arguments",
                $"locator kind '{input.Kind}' has no capability equivalent");
        }

        return new DesktopLocator(kind, input.Value, input.Name, input.Exact, input.Nth, input.HasText);
    }

    private static LocatorKind ParseKind(BrowserLocatorInput input)
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
        return kind;
    }

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
