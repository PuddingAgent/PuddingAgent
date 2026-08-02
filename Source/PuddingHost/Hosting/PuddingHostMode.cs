namespace PuddingHost.Hosting;

/// <summary>
/// Host execution mode — Console (dev server), Desktop (in-process WPF),
/// or DesktopChild (child process launched by Desktop launcher).
/// </summary>
public enum PuddingHostMode
{
    /// <summary>Standalone console dev server.</summary>
    Console,

    /// <summary>In-process within a WPF app (legacy Phase 0, no longer used in Phase 1A).</summary>
    Desktop,

    /// <summary>Child process launched by PuddingDesktop.exe via Process.Start.</summary>
    DesktopChild,
}
