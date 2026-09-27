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
    public DesktopDebugSettings Debug { get; init; } = new();
}

/// <summary>
/// Developer debug mode: Desktop builds Core from source, starts the Admin
/// frontend via `pnpm run start:dev`, and serves a unified loopback entry
/// through its own reverse proxy (ProxyPort). Backend/frontend ports stay
/// separate; only the proxy is the Workbench origin.
/// </summary>
public sealed record DesktopDebugSettings
{
    public bool Enabled { get; init; }
    public string? RepositoryRoot { get; init; }
    public string? FrontendWorkingDirectory { get; init; }
    public string? BackendProjectPath { get; init; }
    public int FrontendPort { get; init; } = 8000;
    public int ProxyPort { get; init; } = 80;
    public int FrontendStartupTimeoutSeconds { get; init; } = 180;
    public int BackendBuildTimeoutSeconds { get; init; } = 300;
}

public sealed record DesktopWindowSettings
{
    public int Width { get; init; } = 1440;
    public int Height { get; init; } = 900;
    public bool IsMaximized { get; init; }
}
