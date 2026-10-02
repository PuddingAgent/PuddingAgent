namespace Pudding.Contracts.Desktop;

/// <summary>
/// 标签页操作（与既有 Bridge 的 <c>page.activate</c>/<c>page.close</c> 等价）。
/// v1 只有这两个：新建/移动/固定标签等不做（Bridge 也没有）。
/// </summary>
public enum DesktopTabAction
{
    /// <summary>把目标页面切为活动页（改变焦点，可能触发页面事件）。</summary>
    /// <summary>新建标签页（加宽 2026-10-02，缺口 #6）：没有已存在的页面可钉版本，因此不钉。</summary>
    New,

    Activate,

    /// <summary>关闭目标页面（破坏性：页面内未保存状态会丢失）。</summary>
    Close,
}

/// <summary>标签页动作线名（真源在本文件，快照由契约测试断言）。</summary>
public static class DesktopTabActionWire
{
    public static string NameOf(DesktopTabAction action) => action switch
    {
        DesktopTabAction.New => "new",
        DesktopTabAction.Activate => "activate",
        DesktopTabAction.Close => "close",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Tab action is not registered."),
    };

    public static bool TryParse(string? name, out DesktopTabAction action)
    {
        foreach (var candidate in Enum.GetValues<DesktopTabAction>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                action = candidate;
                return true;
            }
        }

        action = default;
        return false;
    }
}

/// <summary>
/// 标签页操作请求（变更类）：必须固定页面版本——版本不符说明目标页在等待期间已变化，
/// 此时切换/关闭的可能是**另一个页面**，因此必须拒绝而不是猜。
/// </summary>
public sealed record BrowserTabsRequest
{
    public BrowserTabsRequest(
        DesktopPageTarget target,
        DesktopTabAction action,
        DesktopPageVersion expectedPageVersion,
        Uri? url = null,
        bool activate = true)
        : this(target, contextId: null, action, expectedPageVersion, url, activate)
    {
    }

    /// <summary>
    /// 新建标签页：没有已存在的页面 ⇒ <see cref="Target"/> 为 <c>null</c>、**不钉版本**，
    /// 上下文由 <paramref name="contextId"/> 指明。这是唯一允许不钉版本的标签页动作。
    /// </summary>
    public static BrowserTabsRequest New(string contextId, Uri? url = null, bool activate = true) =>
        new(null, contextId, DesktopTabAction.New, DesktopPageVersion.Unknown, url, activate);

    private BrowserTabsRequest(
        DesktopPageTarget? target,
        string? contextId,
        DesktopTabAction action,
        DesktopPageVersion expectedPageVersion,
        Uri? url,
        bool activate)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Tab action is not registered.");
        }

        if (action == DesktopTabAction.New)
        {
            // 新建没有可钉的版本：调用方必须明确不钉（Unknown），不能悄悄带一个版本进来。
            if (target is not null)
            {
                throw new ArgumentException("Creating a tab has no target page.", nameof(target));
            }

            if (expectedPageVersion.IsKnown)
            {
                throw new ArgumentException(
                    "Creating a tab must not pin a page version (no page exists yet).",
                    nameof(expectedPageVersion));
            }

            if (string.IsNullOrWhiteSpace(contextId))
            {
                throw new ArgumentException("Creating a tab requires a context id.", nameof(contextId));
            }

            ContextId = contextId;
        }
        else
        {
            if (expectedPageVersion.Value <= 0)
            {
                throw new ArgumentException(
                    "Tab operations must pin the page version they act on (mutating capability).",
                    nameof(expectedPageVersion));
            }

            ContextId = (target ?? throw new ArgumentNullException(nameof(target))).ContextId;
        }

        Target = target;
        Action = action;
        ExpectedPageVersion = expectedPageVersion;
        Url = url;
        Activate = activate;
    }

    /// <summary>新建时没有目标页 ⇒ <c>null</c>；其余动作用于既有页面。</summary>
    public DesktopPageTarget? Target { get; }

    /// <summary>动作所在上下文：新建时来自请求本身，其余来自 <see cref="Target"/>。</summary>
    public string ContextId { get; }

    public DesktopTabAction Action { get; }

    public DesktopPageVersion ExpectedPageVersion { get; }

    /// <summary>新建标签页的初始地址（其余动作为 <c>null</c>）。</summary>
    public Uri? Url { get; }

    /// <summary>新建时是否切为活动页（其余动作忽略）。</summary>
    public bool Activate { get; }

    /// <summary>关闭是破坏性操作：审计与准入应据此区别对待。</summary>
    public bool IsDestructive => Action == DesktopTabAction.Close;

    public override string ToString() => Action == DesktopTabAction.New
        ? $"new @{ContextId}"
        : $"{DesktopTabActionWire.NameOf(Action)} @{Target} v{ExpectedPageVersion.Value}";
}

/// <summary>
/// 标签页操作结果：操作后的**活动页状态** + 是否真的关闭了 + 剩余清单。
/// <see cref="Remaining"/> 让调用方立刻知道「现在还有什么」，不必再单独查一次。
/// </summary>
public sealed record DesktopTabsResult
{
    public DesktopTabsResult(
        DesktopPageTarget target,
        DesktopTabAction action,
        DesktopPageState page,
        bool tabClosed,
        DesktopContexts remaining)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Action = action;
        Page = page ?? throw new ArgumentNullException(nameof(page));
        TabClosed = tabClosed;
        Remaining = remaining ?? throw new ArgumentNullException(nameof(remaining));
    }

    public DesktopPageTarget Target { get; }

    public DesktopTabAction Action { get; }

    /// <summary>操作完成后的活动页状态（关闭时是接管焦点的那个页面）。</summary>
    public DesktopPageState Page { get; }

    /// <summary>是否确实关闭了目标页（<c>close</c> 才可能为真）。</summary>
    public bool TabClosed { get; }

    /// <summary>操作后的剩余上下文与页面清单。</summary>
    public DesktopContexts Remaining { get; }

    public override string ToString() =>
        $"{DesktopTabActionWire.NameOf(Action)} @{Target}{(TabClosed ? " closed" : string.Empty)} → {Remaining}";
}
