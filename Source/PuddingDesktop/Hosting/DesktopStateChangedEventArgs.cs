using PuddingDesktop.Core;

namespace PuddingDesktop.Hosting;

/// <summary>
/// Fired when the Desktop coordinator transitions between startup states.
/// ViewModels subscribe to this to update UI bindings. CoreAddress is always
/// the real Core child base address (control plane); WorkbenchAddress is the
/// origin the WebView2 Workbench must load — the debug reverse proxy in debug
/// mode, otherwise identical to CoreAddress.
/// </summary>
public sealed record DesktopStateChangedEventArgs(
    DesktopStartupState Previous,
    DesktopStartupState Current,
    Uri? CoreAddress,
    Uri? WorkbenchAddress,
    string? Error);
