using System;
using System.Collections.Generic;
using System.Linq;

namespace PuddingDesktop.Foundation;

/// <summary>
/// Content family of a tool tab. The list is open: it names the families the Shell
/// renders today, it is not a fixed five-slot tab bar.
/// </summary>
public enum ToolTabKind
{
    /// <summary>Workspace home / new tab: entries for the other families.</summary>
    Home,
    Terminal,
    Browser,
    Artifact,
    Output,
    Panel,
}

/// <summary>
/// Honest lifecycle state. A capability that the Shell cannot execute yet must say so
/// instead of showing a running task or a fake terminal prompt.
/// </summary>
public enum ToolTabAvailability
{
    Ready,
    Deferred,
    Unavailable,
}

public sealed record ToolTabDescriptor
{
    public required string Id { get; init; }
    public required ToolTabKind Kind { get; init; }
    public required string Title { get; init; }

    /// <summary>Owning workspace / session / Agent, shown on the tab tooltip and header.</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// Canonical resource identity for resource-backed tabs (browser page, artifact,
    /// output file, panel). Terminals ignore it: they are explicit new instances.
    /// </summary>
    public string? ResourceKey { get; init; }

    public ToolTabAvailability Availability { get; init; } = ToolTabAvailability.Ready;

    public bool IsRunning { get; init; }

    public string? StatusText { get; init; }
}

/// <summary>One open tab instance. Mutated only through <see cref="ToolWorkspaceTabs"/>.</summary>
public sealed class ToolTab
{
    internal ToolTab(ToolTabDescriptor descriptor)
    {
        Id = descriptor.Id;
        Kind = descriptor.Kind;
        Title = descriptor.Title;
        Subtitle = descriptor.Subtitle;
        ResourceKey = descriptor.ResourceKey;
        Availability = descriptor.Availability;
        IsRunning = descriptor.IsRunning;
        StatusText = descriptor.StatusText;
    }

    public string Id { get; }
    public ToolTabKind Kind { get; }
    public string Title { get; internal set; }
    public string? Subtitle { get; internal set; }
    public string? ResourceKey { get; }
    public ToolTabAvailability Availability { get; internal set; }

    /// <summary>True only while a real execution (terminal process, browser automation) runs.</summary>
    public bool IsRunning { get; internal set; }

    /// <summary>Set when background output arrived while the user was elsewhere.</summary>
    public bool HasUnread { get; internal set; }

    public string? StatusText { get; internal set; }

    /// <summary>The workspace home is the empty-state fallback and cannot be closed.</summary>
    public bool CanClose => Kind != ToolTabKind.Home;

    /// <summary>Tooltip: full title plus the owning Agent/session when known.</summary>
    public string Tooltip => string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title} · {Subtitle}";
}

/// <summary>
/// Single place that decides tab identity, so "open the same resource twice" behaviour
/// is a tested policy instead of a per-call-site convention.
/// </summary>
public static class ToolTabIdentity
{
    public const string Home = "home";

    public static string Browser(string pageId) => "browser:" + (pageId ?? string.Empty).Trim();

    /// <summary>Terminal instances are never de-duplicated: only an explicit new session creates one.</summary>
    public static string Terminal(string sessionId) => "terminal:" + (sessionId ?? string.Empty).Trim();

    public static string Artifact(string resourceKey) => "artifact:" + NormalizeResourceKey(resourceKey);

    public static string Output(string resourceKey) => "output:" + NormalizeResourceKey(resourceKey);

    public static string Panel(string resourceKey) => "panel:" + NormalizeResourceKey(resourceKey);

    /// <summary>
    /// File-system resources are case-insensitive on Windows and use one separator, so a
    /// path re-opened with a different casing activates the existing tab. Opaque ids stay
    /// case-sensitive.
    /// </summary>
    public static string NormalizeResourceKey(string? resourceKey)
    {
        var trimmed = (resourceKey ?? string.Empty).Trim();
        if (trimmed.Length == 0) return trimmed;
        return trimmed.Contains('\\') || trimmed.Contains('/')
            ? trimmed.Replace('/', '\\').ToLowerInvariant()
            : trimmed;
    }
}

/// <summary>
/// Instance registry for the right-hand tool workspace. Holds no UI dependency: the
/// WinUI Shell projects it into tab headers, so the open/activate/close/unread rules
/// stay unit-testable without a window.
/// </summary>
public sealed class ToolWorkspaceTabs
{
    private readonly List<ToolTab> _tabs = [];

    public IReadOnlyList<ToolTab> Tabs => _tabs;

    public string? ActiveTabId { get; private set; }

    public ToolTab? ActiveTab => Find(ActiveTabId);

    public int RunningCount => _tabs.Count(tab => tab.IsRunning);

    public int UnreadCount => _tabs.Count(tab => tab.HasUnread);

    public ToolTab? Find(string? tabId) =>
        string.IsNullOrEmpty(tabId) ? null : _tabs.FirstOrDefault(tab => tab.Id == tabId);

    /// <summary>
    /// Opens or focuses the instance identified by <see cref="ToolTabDescriptor.Id"/>.
    /// <paramref name="focus"/> false keeps the current tab and only raises an unread
    /// marker, so background Agent output never steals the tab or keyboard focus.
    /// </summary>
    public ToolTab Open(ToolTabDescriptor descriptor, bool focus = true)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Id))
            throw new ArgumentException("A tool tab needs a stable instance id.", nameof(descriptor));

        var existing = Find(descriptor.Id);
        if (existing is not null)
        {
            existing.Title = descriptor.Title;
            existing.Subtitle = descriptor.Subtitle ?? existing.Subtitle;
            existing.Availability = descriptor.Availability;
            existing.StatusText = descriptor.StatusText;
            return focus ? ActivateInternal(existing) : MarkUnreadInternal(existing);
        }

        var tab = new ToolTab(descriptor);
        _tabs.Add(tab);
        // Something must stay selected: a request that arrives while the workspace is
        // empty becomes the active tab even when it asked not to steal focus.
        return focus || ActiveTabId is null ? ActivateInternal(tab) : MarkUnreadInternal(tab);
    }

    /// <summary>Selects a tab and clears its unread marker.</summary>
    public bool Activate(string? tabId)
    {
        var tab = Find(tabId);
        if (tab is null) return false;
        ActivateInternal(tab);
        return true;
    }

    /// <summary>Closes a tab and leaves the nearest remaining tab selected.</summary>
    public bool Close(string? tabId)
    {
        var tab = Find(tabId);
        if (tab is null || !tab.CanClose) return false;

        var index = _tabs.IndexOf(tab);
        _tabs.RemoveAt(index);
        if (ActiveTabId == tab.Id || ActiveTabId is null)
        {
            var neighbour = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
            ActiveTabId = neighbour?.Id;
            if (neighbour is not null) neighbour.HasUnread = false;
        }
        return true;
    }

    /// <summary>Ensures the singleton workspace home tab exists and puts it first.</summary>
    public ToolTab EnsureHome()
    {
        var home = Find(ToolTabIdentity.Home);
        if (home is null)
        {
            home = new ToolTab(new ToolTabDescriptor
            {
                Id = ToolTabIdentity.Home,
                Kind = ToolTabKind.Home,
                Title = "工具首页",
                Subtitle = "打开终端、浏览器或成果",
            });
            _tabs.Insert(0, home);
            ActiveTabId ??= home.Id;
        }
        return home;
    }

    public bool Rename(string? tabId, string title, string? subtitle = null)
    {
        var tab = Find(tabId);
        if (tab is null) return false;
        tab.Title = title;
        if (subtitle is not null) tab.Subtitle = subtitle;
        return true;
    }

    public bool SetRunning(string? tabId, bool running)
    {
        var tab = Find(tabId);
        if (tab is null || tab.IsRunning == running) return false;
        tab.IsRunning = running;
        return true;
    }

    /// <summary>Updates the tab status line and, for deferred capabilities, its honesty state.</summary>
    public bool SetStatus(string? tabId, string? statusText, ToolTabAvailability? availability = null)
    {
        var tab = Find(tabId);
        if (tab is null) return false;
        var changed = tab.StatusText != statusText || (availability is not null && tab.Availability != availability);
        tab.StatusText = statusText;
        if (availability is not null) tab.Availability = availability.Value;
        return changed;
    }

    public bool MarkUnread(string? tabId)
    {
        var tab = Find(tabId);
        if (tab is null || tab.HasUnread) return false;
        tab.HasUnread = true;
        return true;
    }

    /// <summary>Short summary for the collapsed entry button; empty when nothing needs attention.</summary>
    public string DescribeActivity()
    {
        var running = RunningCount;
        var unread = UnreadCount;
        return (running, unread) switch
        {
            (0, 0) => string.Empty,
            (_, 0) => $"{running} 项运行中",
            (0, _) => $"{unread} 项未读",
            _ => $"{running} 项运行中 · {unread} 项未读",
        };
    }

    private ToolTab ActivateInternal(ToolTab tab)
    {
        ActiveTabId = tab.Id;
        tab.HasUnread = false;
        return tab;
    }

    private ToolTab MarkUnreadInternal(ToolTab tab)
    {
        // The selected tab is by definition already seen.
        if (tab.Kind != ToolTabKind.Home && tab.Id != ActiveTabId) tab.HasUnread = true;
        return tab;
    }
}

/// <summary>
/// Decides whether background activity may open the tool workspace by itself.
/// A manual collapse suppresses auto-expand for the rest of the current activity
/// round; once everything is idle the suppression is dropped.
/// </summary>
public sealed class ToolWorkspaceActivityPolicy
{
    public bool AutoExpandOnActivity { get; set; }

    public bool IsSuppressedByUser { get; private set; }

    /// <summary>The user collapsed the workspace while work was in flight.</summary>
    public void NotifyUserCollapsed() => IsSuppressedByUser = true;

    /// <summary>The user opened the workspace themselves; they are watching now.</summary>
    public void NotifyUserExpanded() => IsSuppressedByUser = false;

    /// <summary>A round ends when no tool or Agent activity is in flight.</summary>
    public bool Observe(bool activityInFlight)
    {
        if (activityInFlight || !IsSuppressedByUser) return false;
        IsSuppressedByUser = false;
        return true;
    }

    public bool ShouldAutoExpand() => AutoExpandOnActivity && !IsSuppressedByUser;
}
