namespace PuddingBrowser.Abstractions;

/// <summary>Locator strategy for finding DOM elements.</summary>
public enum LocatorKind
{
    Ref,
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

/// <summary>
/// Safe, by-value metadata returned by locator resolution. It contains no DOM object,
/// JavaScript handle, form value, cookie, or authentication secret.
/// </summary>
/// <remarks>
/// <see cref="Value"/>/<see cref="SelectedValues"/> 是**输入是否写入成功**的唯一可靠回执
/// （设计方案 §6.2/§4.2）：<c>text</c> 不含表单值，导航前的 URL 也推不出填了什么。
/// 它们是普通按值字符串，因此上层必须按敏感策略处理（脱敏/只留长度），不得直接回显到日志或工具回执。
/// </remarks>
public sealed record BrowserElementInfo
{
    public required string Ref { get; init; }
    public required string Tag { get; init; }
    public string? Role { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public bool Visible { get; init; }
    public bool Enabled { get; init; }
    public bool? Checked { get; init; }

    /// <summary>input/textarea/contenteditable 的当前值；非表单元素为 <c>null</c>。</summary>
    public string? Value { get; init; }

    /// <summary><c>select</c> 当前选中的值；非 select 为 <c>null</c>。</summary>
    public IReadOnlyList<string>? SelectedValues { get; init; }

    public BoundingBox? BoundingBox { get; init; }
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
