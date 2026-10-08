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
    public int MaxDepth { get; init; } = 24;

    /// <summary>
    /// 读取作用域（设计方案 §151 的 <c>scope</c>）：只从该 ref 的节点（含自己）开始遍历。
    /// <c>null</c> = 整页；引用过期由驱动明确拒绝（<c>stale_element_reference</c>），不退化成正页。
    /// </summary>
    public string? ScopeRef { get; init; }

    /// <summary>与上次读取的修订标识比较（§145）；由**持有基线的那一侧**解释。</summary>
    public string? SinceRevision { get; init; }
}

public sealed record PageSnapshot
{
    public string? DomText { get; init; }
    public string? AccessibilityTree { get; init; }
    public string? Html { get; init; }
    public bool Truncated { get; init; }
    public int NodeCount { get; init; }

    /// <summary>本次读取的修订标识（§145）；由**持有基线的那一侧**签发。</summary>
    public string? Revision { get; init; }

    /// <summary>与调用方给的修订相比内容是否变化；未要求比较时为 <c>null</c>。</summary>
    public bool? Changed { get; init; }

    /// <summary>行级差异；形状在 Abstractions 里保持中立（本组件不得引用 Contracts）。</summary>
    public TextDiff? Diff { get; init; }
}

/// <summary>
/// 行级差异的中立形状（Abstractions）：契约层的 <c>BrowserTextDiff</c> 是它的对外投影。
/// 计数是真实的，列表可能因预算被截断。
/// </summary>
public sealed record TextDiff
{
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
    public int AddedCount { get; init; }
    public int RemovedCount { get; init; }
    public bool Truncated { get; init; }
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
