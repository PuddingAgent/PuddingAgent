using System.Collections.ObjectModel;

namespace PuddingBrowser.AgentTools;

/// <summary>
/// Canonical tool IDs for the **eight** browser Agent tools (ADR-097 D1/D5).
/// The All collection defines the fixed enumeration order:
/// Context, Tabs, Navigate, Snapshot, Screenshot, Locate, Interact, WaitFor.
///
/// “工具存在”与“该动作支持”是两件事：All 是**事实目录**（注册/发现/提示词/测试共用），
/// 运行时支持集由 Host/Desktop 的能力握手如实表达。
/// </summary>
public static class BrowserAgentToolIds
{
    public const string Context = "browser_context";
    public const string Tabs = "browser_tabs";
    public const string Navigate = "browser_navigate";
    public const string Snapshot = "browser_snapshot";
    /// <summary>截图（§8.1）：结果携带 observationId，供后续坐标操作解析。</summary>
    public const string Screenshot = "browser_screenshot";
    public const string Locate = "browser_locate";
    public const string Interact = "browser_interact";
    public const string WaitFor = "browser_wait_for";

    public static readonly IReadOnlyList<string> All = new ReadOnlyCollection<string>(
    [
        Context,
        Tabs,
        Navigate,
        Snapshot,
        Screenshot,
        Locate,
        Interact,
        WaitFor
    ]);
}

/// <summary>
/// Canonical capability IDs for the **eight** browser Agent capabilities.
/// The All collection defines the same fixed enumeration order as BrowserAgentToolIds.
/// </summary>
public static class BrowserAgentCapabilityIds
{
    public const string Context = "cap-browser-context";
    public const string Tabs = "cap-browser-tabs";
    public const string Navigate = "cap-browser-navigate";
    public const string Snapshot = "cap-browser-snapshot";
    public const string Screenshot = "cap-browser-screenshot";
    public const string Locate = "cap-browser-locate";
    public const string Interact = "cap-browser-interact";
    public const string WaitFor = "cap-browser-wait-for";

    public static readonly IReadOnlyList<string> All = new ReadOnlyCollection<string>(
    [
        Context,
        Tabs,
        Navigate,
        Snapshot,
        Screenshot,
        Locate,
        Interact,
        WaitFor
    ]);
}
