namespace Pudding.Contracts.Desktop;

/// <summary>
/// 标签页操作（与既有 Bridge 的 <c>page.activate</c>/<c>page.close</c> 等价）。
/// v1 只有这两个：新建/移动/固定标签等不做（Bridge 也没有）。
/// </summary>
public enum DesktopTabAction
{
    /// <summary>把目标页面切为活动页（改变焦点，可能触发页面事件）。</summary>
    Activate,

    /// <summary>关闭目标页面（破坏性：页面内未保存状态会丢失）。</summary>
    Close,
}

/// <summary>标签页动作线名（真源在本文件，快照由契约测试断言）。</summary>
public static class DesktopTabActionWire
{
    public static string NameOf(DesktopTabAction action) => action switch
    {
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
        DesktopPageVersion expectedPageVersion)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Tab action is not registered.");
        }

        if (expectedPageVersion.Value <= 0)
        {
            throw new ArgumentException(
                "Tab operations must pin the page version they act on (mutating capability).",
                nameof(expectedPageVersion));
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        Action = action;
        ExpectedPageVersion = expectedPageVersion;
    }

    public DesktopPageTarget Target { get; }

    public DesktopTabAction Action { get; }

    public DesktopPageVersion ExpectedPageVersion { get; }

    /// <summary>关闭是破坏性操作：审计与准入应据此区别对待。</summary>
    public bool IsDestructive => Action == DesktopTabAction.Close;

    public override string ToString() => $"{DesktopTabActionWire.NameOf(Action)} @{Target} v{ExpectedPageVersion.Value}";
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
