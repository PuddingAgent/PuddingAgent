namespace PuddingHost.Hosting;

/// <summary>
/// Host execution mode — Console (dev server), Desktop (in-process WinUI),
/// or DesktopChild (child process launched by Desktop launcher).
/// </summary>
public enum PuddingHostMode
{
    /// <summary>Standalone console dev server.</summary>
    Console,

    /// <summary>Product Core DLL within the WinUI Desktop process.</summary>
    Desktop,

    /// <summary>Historical WPF child mode, retained for migration/development only.</summary>
    DesktopChild,
}
