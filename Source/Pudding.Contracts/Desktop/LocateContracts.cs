namespace Pudding.Contracts.Desktop;

/// <summary>
/// 定位策略（与 <c>PuddingBrowser.Abstractions.LocatorKind</c> 等价，取值为线名真源）。
/// v1 <b>不支持</b>复合定位（Has）与跨帧选择（Frame）：线缆上无法表达，因而不接受。
/// </summary>
public enum DesktopLocatorKind
{
    /// <summary>快照返回的元素引用；只在同一 PageVersion 内有效。</summary>
    Ref,

    Css,
    XPath,
    Text,
    Role,
    Label,
    Placeholder,
    AltText,
    Title,
    TestId,
}

/// <summary>定位策略线名（kebab-case；真源在本文件，快照由契约测试断言）。</summary>
public static class DesktopLocatorKindWire
{
    public static string NameOf(DesktopLocatorKind kind) => kind switch
    {
        DesktopLocatorKind.Ref => "ref",
        DesktopLocatorKind.Css => "css",
        DesktopLocatorKind.XPath => "xpath",
        DesktopLocatorKind.Text => "text",
        DesktopLocatorKind.Role => "role",
        DesktopLocatorKind.Label => "label",
        DesktopLocatorKind.Placeholder => "placeholder",
        DesktopLocatorKind.AltText => "alt-text",
        DesktopLocatorKind.Title => "title",
        DesktopLocatorKind.TestId => "test-id",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Locator kind is not registered."),
    };

    /// <summary>严格解析：不知道的线名不猜测、不静默回退（fail closed）。</summary>
    public static bool TryParse(string? name, out DesktopLocatorKind kind)
    {
        foreach (var candidate in Enum.GetValues<DesktopLocatorKind>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>定位描述符：策略 + 值 + 可选修饰。含 <see cref="Nth"/> 与 <see cref="HasText"/> 的复合语义。</summary>
public sealed record DesktopLocator
{
    public const int MaxValueLength = 2048;

    public const int MaxTextLength = 512;

    public DesktopLocator(
        DesktopLocatorKind kind,
        string value,
        string? name = null,
        bool exact = false,
        int? nth = null,
        string? hasText = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Locator kind is not registered.");
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength)
        {
            throw new ArgumentException(
                $"Locator value must be 1..{MaxValueLength} characters.", nameof(value));
        }

        if (name is { Length: > MaxValueLength })
        {
            throw new ArgumentException($"Locator name must be at most {MaxValueLength} characters.", nameof(name));
        }

        if (nth is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nth), nth, "Nth must not be negative.");
        }

        if (hasText is { Length: > MaxTextLength })
        {
            throw new ArgumentException($"HasText must be at most {MaxTextLength} characters.", nameof(hasText));
        }

        Kind = kind;
        Value = value;
        Name = string.IsNullOrEmpty(name) ? null : name;
        Exact = exact;
        Nth = nth;
        HasText = string.IsNullOrEmpty(hasText) ? null : hasText;
    }

    public DesktopLocatorKind Kind { get; }

    public string Value { get; }

    public string? Name { get; }

    public bool Exact { get; }

    public int? Nth { get; }

    public string? HasText { get; }

    /// <summary>Ref 定位是「凭快照引用」：调用方必须保证引用来自同一个 PageVersion。</summary>
    public bool IsReference => Kind == DesktopLocatorKind.Ref;

    public override string ToString() =>
        $"{DesktopLocatorKindWire.NameOf(Kind)}({Value}{(Nth is { } nth ? $"#{nth}" : string.Empty)})";
}

/// <summary>定位请求：显式页面目标 + 定位描述符 + 期望页面版本 + 结果上限。</summary>
public sealed record BrowserLocateRequest
{
    public const int DefaultMaxResults = 20;

    public const int MaxMaxResults = 100;

    public BrowserLocateRequest(
        DesktopPageTarget target,
        DesktopLocator locator,
        DesktopPageVersion expectedPageVersion = default,
        int maxResults = DefaultMaxResults)
    {
        if (maxResults is < 1 or > MaxMaxResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults), maxResults, $"Result limit must be in [1, {MaxMaxResults}].");
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        ExpectedPageVersion = expectedPageVersion;
        MaxResults = maxResults;
    }

    public DesktopPageTarget Target { get; }

    public DesktopLocator Locator { get; }

    public DesktopPageVersion ExpectedPageVersion { get; }

    public int MaxResults { get; }
}

/// <summary>
/// 元素引用（按值元数据，不含 DOM 句柄/表单值/凭据）。
/// <b>不变量</b>：<see cref="PageVersion"/> 必须有效；交互提交后不得复用旧 Ref。
/// </summary>
public sealed record DesktopElementRef
{
    public DesktopElementRef(
        string reference,
        string tag,
        DesktopPageVersion pageVersion,
        string? role = null,
        string? name = null,
        string? text = null,
        bool visible = true,
        bool enabled = true,
        bool? isChecked = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException("Element reference must be non-empty.", nameof(reference));
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Element tag must be non-empty.", nameof(tag));
        }

        if (pageVersion.Value <= 0)
        {
            throw new ArgumentException(
                "Element reference requires a live page version (refs die with the page version).",
                nameof(pageVersion));
        }

        Reference = reference;
        Tag = tag;
        PageVersion = pageVersion;
        Role = role;
        Name = name;
        Text = text;
        Visible = visible;
        Enabled = enabled;
        IsChecked = isChecked;
    }

    public string Reference { get; }

    public string Tag { get; }

    /// <summary>该引用所属的页面版本；调用方凭它判断引用是否仍然有效。</summary>
    public DesktopPageVersion PageVersion { get; }

    public string? Role { get; }

    public string? Name { get; }

    public string? Text { get; }

    public bool Visible { get; }

    public bool Enabled { get; }

    public bool? IsChecked { get; }

    public override string ToString() => $"{Reference} <{Tag}> v{PageVersion.Value}";
}

/// <summary>定位结果：命中的元素引用（可能为空）+ 是否被截断 + 快照版本。</summary>
public sealed record DesktopLocateResult
{
    public DesktopLocateResult(
        DesktopPageTarget target,
        DesktopLocator locator,
        IReadOnlyList<DesktopElementRef> elements,
        bool truncated,
        DesktopPageVersion pageVersion)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        Elements = elements ?? throw new ArgumentNullException(nameof(elements));
        Truncated = truncated;
        PageVersion = pageVersion;
    }

    public DesktopPageTarget Target { get; }

    public DesktopLocator Locator { get; }

    public IReadOnlyList<DesktopElementRef> Elements { get; }

    public bool Truncated { get; }

    public DesktopPageVersion PageVersion { get; }

    /// <summary>命中数量为 0 不是错误：调用方据此决定等待还是改定位策略。</summary>
    public bool IsEmpty => Elements.Count == 0;

    public override string ToString() =>
        $"locate {Locator} @{Target} → {Elements.Count} element(s) v{PageVersion.Value} truncated={Truncated}";
}
