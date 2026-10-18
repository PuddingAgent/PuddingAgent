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
        string? hasText = null,
        bool includeHidden = false,
        bool inViewportOnly = false)
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
        IncludeHidden = includeHidden;
        InViewportOnly = inViewportOnly;
    }

    public DesktopLocatorKind Kind { get; }

    public string Value { get; }

    public string? Name { get; }

    public bool Exact { get; }

    public int? Nth { get; }

    public string? HasText { get; }

    /// <summary>
    /// 是否把隐藏元素也算作匹配（设计方案 §6.2：locate 默认 <c>false</c>）。
    ///
    /// 与 <c>visible</c>/<c>inViewport</c> 的关系：<c>false</c> 只排除**隐藏**元素
    /// （display/visibility/opacity 或零尺寸）；**屏外但有尺寸**的元素仍可定位 ——
    /// 「在视口内」是另一个事实，不能混为一谈。
    /// </summary>
    public bool IncludeHidden { get; }

    /// <summary>只返回**与视口相交**的元素（默认 <c>false</c>）。与 <see cref="IncludeHidden"/> 正交。</summary>
    public bool InViewportOnly { get; }

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
        int maxResults = DefaultMaxResults,
        string? cursor = null)
    {
        if (maxResults is < 1 or > MaxMaxResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults), maxResults, $"Result limit must be in [1, {MaxMaxResults}].");
        }

        // 「Ref 随页面版本失效」在边界上强制：凭引用定位却不说明引用来自哪一版，
        // 接收方就无法判定它是否已失效，因此这种请求必须被拒绝而不是猜测。
        if (locator is { IsReference: true } && expectedPageVersion.Value <= 0)
        {
            throw new ArgumentException(
                "A reference locator must carry the page version the reference came from.",
                nameof(expectedPageVersion));
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        ExpectedPageVersion = expectedPageVersion;
        MaxResults = maxResults;
        Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
    }

    public DesktopPageTarget Target { get; }

    public DesktopLocator Locator { get; }

    public DesktopPageVersion ExpectedPageVersion { get; }

    public int MaxResults { get; }

    /// <summary>
    /// 分页游标（对模型不透明，由宿主签发与解释；见 <see cref="BrowserLocateCursor"/>）。
    /// 传旧游标去匹配新内容会被拒绝（<c>cursor_expired</c>），而不是返回错位的结果。
    /// </summary>
    public string? Cursor { get; }
}

/// <summary>
/// 元素包围盒（可选）。加宽（2026-10-02）：工具侧 <c>BrowserElementToolValue.BoundingBox</c> 一直存在，
/// 而契约/线缆此前没有 ⇒ 能力通道拿不到、Bridge 拿得到。
/// </summary>
public sealed record DesktopElementBox(double X, double Y, double Width, double Height);

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
        bool? isChecked = null,
        string? value = null,
        IReadOnlyList<string>? selectedValues = null,
        bool? inViewport = null)
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
        Value = value;
        SelectedValues = selectedValues;
        InViewport = inViewport;
    }

    /// <summary>元素包围盒（不知道时为 <c>null</c>——绝不用 0 假装知道）。</summary>
    public DesktopElementBox? BoundingBox { get; init; }

    /// <summary>
    /// input/textarea/contenteditable 的当前值；非表单元素为 <c>null</c>。
    ///
    /// 为什么必须有（设计方案 §4.2/§6.2）：<c>Text</c> 不含表单值，因此「fill 到底写进去了没有」
    /// 在仅有 text/URL 时无法判定。回执按**敏感策略**处理它：密码类只留长度与 matched，
    /// 普通值有长度上限（见 <c>BrowserActionAssertion</c>）。
    /// </summary>
    public string? Value { get; }

    /// <summary><c>select</c> 当前选中的值；非 select 为 <c>null</c>。</summary>
    public IReadOnlyList<string>? SelectedValues { get; }

    /// <summary>
    /// 元素是否与视口相交。**与 <see cref="Visible"/> 是两个不同的事实**（设计方案 §6.2）：
    /// 屏外但有尺寸的元素可见却不在视口内；写操作要求先滚入可见区域，因此不能只看 visible。
    /// 未观测为 <c>null</c>（绝不用 <c>false</c> 假装观测过）。
    /// </summary>
    public bool? InViewport { get; }

    public string Reference { get; }

    public string Tag { get; }

    /// <summary>该引用所属的页面版本；调用方凭它判断引用是否仍然有效。</summary>
    public DesktopPageVersion PageVersion { get; }

    /// <summary>
    /// 该元素的 Value/SelectedValues/IsChecked 是**动作提交之前**的快照。
    ///
    /// 交互类结果必须如实标注：动作前观察只能用于"这里原本是什么"，
    /// **不能**当作动作后验证（expect_value/select/check 的证据）——那是 F5 的谎报形态。
    /// 需要动作后证据时必须由执行端在动作后回读，而不是把本字段当默认可用。
    /// </summary>
    public bool IsPreActionObservation { get; init; }

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
        DesktopPageVersion pageVersion,
        string? nextCursor = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        Elements = elements ?? throw new ArgumentNullException(nameof(elements));
        Truncated = truncated;
        PageVersion = pageVersion;
        NextCursor = string.IsNullOrWhiteSpace(nextCursor) ? null : nextCursor;
    }

    public DesktopPageTarget Target { get; }

    public DesktopLocator Locator { get; }

    public IReadOnlyList<DesktopElementRef> Elements { get; }

    public bool Truncated { get; }

    public DesktopPageVersion PageVersion { get; }

    /// <summary>
    /// 下一页游标；<c>null</c> 表示**已经取完**（不是"取不到"）。
    /// 与 <see cref="Truncated"/> 分开：截断也可能是"驱动扫描窗口用尽"，那种情况同样给不出游标。
    /// </summary>
    public string? NextCursor { get; }

    /// <summary>命中数量为 0 不是错误：调用方据此决定等待还是改定位策略。</summary>
    public bool IsEmpty => Elements.Count == 0;

    public override string ToString() =>
        $"locate {Locator} @{Target} → {Elements.Count} element(s) v{PageVersion.Value} truncated={Truncated}";
}
