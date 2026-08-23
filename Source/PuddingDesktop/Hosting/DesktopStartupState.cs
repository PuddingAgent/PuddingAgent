namespace PuddingDesktop.Hosting;

/// <summary>
/// Desktop application startup and runtime state.
/// The launcher window MUST remain operable in all states.
/// </summary>
public enum DesktopStartupState
{
    NeedsDataRoot,
    InvalidConfiguration,
    CoreStopped,
    CoreStarting,
    CoreReady,
    CoreStopping,
    CoreFailed,
    CoreRestartScheduled,
    CoreCircuitOpen,
    WebViewInitializing,
    WorkbenchReady,
    WorkbenchFailed,
    DebugFailed,
}
