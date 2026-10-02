namespace Pudding.Contracts.Desktop;

/// <summary>
/// 页面摘要（按值元数据，不含 DOM 句柄/表单值/凭据）——用于列出标签页与上下文。
/// <b>不变量</b>：<see cref="Version"/> 必须有效（与 <see cref="DesktopElementRef"/> 同理）。
/// </summary>
public sealed record DesktopPageInfo
{
    public DesktopPageInfo(
        DesktopPageTarget target,
        DesktopPageVersion version,
        string? title = null,
        Uri? url = null,
        bool isActive = false,
        bool isAgentTarget = false,
        bool canGoBack = false,
        bool canGoForward = false,
        bool isLoading = false)
    {
        if (version.Value <= 0)
        {
            throw new ArgumentException(
                "Page info requires a live page version (references die with the page version).",
                nameof(version));
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        Version = version;
        Title = string.IsNullOrEmpty(title) ? null : title;
        Url = url;
        IsActive = isActive;
        IsAgentTarget = isAgentTarget;
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        IsLoading = isLoading;
    }

    public DesktopPageTarget Target { get; }

    public DesktopPageVersion Version { get; }

    public string? Title { get; }

    public Uri? Url { get; }

    /// <summary>是否为当前活动页面（同一上下文内有且最多一个）。</summary>
    public bool IsActive { get; }

    /// <summary>是否为 Agent 授权目标。</summary>
    public bool IsAgentTarget { get; }

    public bool CanGoBack { get; }

    public bool CanGoForward { get; }

    public bool IsLoading { get; }

    public override string ToString() => $"{Target} v{Version.Value}{(IsActive ? " active" : string.Empty)}";
}

/// <summary>浏览器上下文摘要：可信级别 + 页面列表。</summary>
public sealed record DesktopContextInfo
{
    public DesktopContextInfo(
        string contextId,
        DesktopContextTrust trust,
        IReadOnlyList<DesktopPageInfo> pages)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            throw new ArgumentException("Context id must be non-empty.", nameof(contextId));
        }

        if (!Enum.IsDefined(trust))
        {
            throw new ArgumentOutOfRangeException(nameof(trust), trust, "Context trust is not registered.");
        }

        ContextId = contextId;
        Trust = trust;
        Pages = pages ?? throw new ArgumentNullException(nameof(pages));
    }

    public string ContextId { get; }

    public DesktopContextTrust Trust { get; }

    public IReadOnlyList<DesktopPageInfo> Pages { get; }

    /// <summary>
    /// 是否为持久化上下文（user-data-dir 落盘）。
    /// 加宽（2026-10-02）：工具侧 `BrowserContextToolValue.Persistent` 一直存在，
    /// 而契约/线缆此前没有 ⇒ 能力通道拿不到、Bridge 拿得到。缺省 <c>false</c>（"未声明即非持久"），
    /// 与线缆上的 `bool`（无 presence）语义一致。
    /// </summary>
    public bool Persistent { get; init; }

    public int PageCount => Pages.Count;

    public override string ToString() => $"{ContextId} [{Trust}] pages={PageCount}";
}

/// <summary>
/// 上下文与页面清单（只读，浏览器作用域）。
/// 这是「先看清有什么」的能力：拿到目标与版本后，后续调用才能带上显式目标与版本约束。
/// </summary>
public sealed record DesktopContexts
{
    public DesktopContexts(IReadOnlyList<DesktopContextInfo> contexts, DesktopPageVersion observedVersion = default)
    {
        Contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        ObservedVersion = observedVersion;
    }

    public IReadOnlyList<DesktopContextInfo> Contexts { get; }

    /// <summary>可选：清单发布时的页面版本（若由单个活动页面推导得出）。</summary>
    public DesktopPageVersion ObservedVersion { get; }

    public int PageCount => Contexts.Sum(context => context.PageCount);

    public bool IsEmpty => Contexts.Count == 0;

    public override string ToString() => $"contexts={Contexts.Count} pages={PageCount}";
}
