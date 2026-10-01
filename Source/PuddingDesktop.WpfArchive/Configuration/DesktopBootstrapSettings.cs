namespace PuddingDesktop.Configuration;

/// <summary>
/// Minimal desktop launcher configuration stored in %LOCALAPPDATA%\Pudding\desktop.json.
/// Only stores DataRoot, optional Core path, window geometry and Shell layout preferences.
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
    public DesktopToolWorkspaceSettings ToolWorkspace { get; init; } = new();
    public DesktopDebugSettings Debug { get; init; } = new();
}

/// <summary>
/// Layout preference for the right-hand multi-tab tool workspace.
/// Only layout survives a restart: which tabs were open, their Agent targets and any
/// running process state belong to the Core/Shell session, never to this file.
/// </summary>
public sealed record DesktopToolWorkspaceSettings
{
    /// <summary>Share of the combined chat + tool width; re-clamped against each window.</summary>
    public double WidthRatio { get; init; } = 0.45;

    /// <summary>Off by default: activity raises a marker instead of interrupting typing.</summary>
    public bool AutoExpandOnActivity { get; init; }

    /// <summary>
    /// The workspace starts collapsed on every launch, so no <c>IsExpanded</c> field is
    /// persisted; an invalid ratio falls back to the default instead of a broken split.
    /// The bounds match the Shell layout guard so a saved preference round-trips unchanged.
    /// </summary>
    public DesktopToolWorkspaceSettings Normalize() => this with
    {
        WidthRatio = double.IsFinite(WidthRatio) ? Math.Clamp(WidthRatio, 0.1, 0.9) : 0.45
    };
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
