namespace PuddingDesktop.Configuration;

/// <summary>
/// Minimal desktop launcher configuration stored in %LOCALAPPDATA%\Pudding\desktop.json.
/// Only stores DataRoot, optional Core path, and window geometry.
/// Must NOT contain Agent, model, port, token, or any Core business config.
/// </summary>
public sealed record DesktopBootstrapSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string? DataRoot { get; init; }
    public string? CoreExecutablePath { get; init; }
    public Runtime.DesktopCloseBehavior CloseBehavior { get; init; } = Runtime.DesktopCloseBehavior.MinimizeToTray;
    public bool StartWithWindows { get; init; }
    public DesktopWindowSettings Window { get; init; } = new();
}

public sealed record DesktopWindowSettings
{
    public int Width { get; init; } = 1440;
    public int Height { get; init; } = 900;
    public bool IsMaximized { get; init; }
}
