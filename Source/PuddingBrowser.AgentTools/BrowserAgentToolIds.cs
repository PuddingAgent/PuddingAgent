using System.Collections.ObjectModel;

namespace PuddingBrowser.AgentTools;

/// <summary>
/// Canonical tool IDs for the seven browser Agent tools.
/// The All collection defines the fixed enumeration order:
/// Context, Tabs, Navigate, Snapshot, Locate, Interact, WaitFor.
/// </summary>
public static class BrowserAgentToolIds
{
    public const string Context = "browser_context";
    public const string Tabs = "browser_tabs";
    public const string Navigate = "browser_navigate";
    public const string Snapshot = "browser_snapshot";
    public const string Locate = "browser_locate";
    public const string Interact = "browser_interact";
    public const string WaitFor = "browser_wait_for";

    public static readonly IReadOnlyList<string> All = new ReadOnlyCollection<string>(
    [
        Context,
        Tabs,
        Navigate,
        Snapshot,
        Locate,
        Interact,
        WaitFor
    ]);
}

/// <summary>
/// Canonical capability IDs for the seven browser Agent capabilities.
/// The All collection defines the same fixed enumeration order as BrowserAgentToolIds.
/// </summary>
public static class BrowserAgentCapabilityIds
{
    public const string Context = "cap-browser-context";
    public const string Tabs = "cap-browser-tabs";
    public const string Navigate = "cap-browser-navigate";
    public const string Snapshot = "cap-browser-snapshot";
    public const string Locate = "cap-browser-locate";
    public const string Interact = "cap-browser-interact";
    public const string WaitFor = "cap-browser-wait-for";

    public static readonly IReadOnlyList<string> All = new ReadOnlyCollection<string>(
    [
        Context,
        Tabs,
        Navigate,
        Snapshot,
        Locate,
        Interact,
        WaitFor
    ]);
}
