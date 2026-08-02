namespace PuddingHost.Hosting;

/// <summary>
/// Options controlling PuddingHost startup and behavior.
/// Shared between Console, Desktop, and DesktopChild modes.
/// </summary>
public sealed record PuddingHostOptions
{
    /// <summary>Console, Desktop, or DesktopChild.</summary>
    public required PuddingHostMode Mode { get; init; }

    /// <summary>Root data directory (PUDDING_DATA_ROOT / --data-root).</summary>
    public required string DataRoot { get; init; }

    /// <summary>HTTP listen URLs.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>Whether to serve the Admin SPA from wwwroot.</summary>
    public bool ServeAdminSpa { get; init; } = true;

    /// <summary>Whether to open the admin in an external browser (Console only).</summary>
    public bool OpenExternalBrowser { get; init; }

    /// <summary>Whether WebView2 browser automation is available (Desktop only).</summary>
    public bool BrowserAutomationEnabled { get; init; }

    /// <summary>Parent process ID for DesktopChild mode (used for orphan detection).</summary>
    public int? DesktopParentPid { get; init; }

}
