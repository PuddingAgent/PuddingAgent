using PuddingDesktop.Core;

namespace PuddingDesktop.Hosting;

/// <summary>
/// Fired when the Desktop coordinator transitions between startup states.
/// ViewModels subscribe to this to update UI bindings.
/// </summary>
public sealed record DesktopStateChangedEventArgs(
    DesktopStartupState Previous,
    DesktopStartupState Current,
    Uri? CoreAddress,
    string? Error);
