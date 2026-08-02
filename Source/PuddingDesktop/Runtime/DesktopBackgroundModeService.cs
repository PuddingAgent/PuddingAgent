namespace PuddingDesktop.Runtime;

public sealed class DesktopBackgroundModeService
{
    private int _explicitExitRequested;

    public DesktopCloseBehavior CloseBehavior { get; private set; } = DesktopCloseBehavior.MinimizeToTray;
    public bool IsExplicitExitRequested => Volatile.Read(ref _explicitExitRequested) != 0;

    public void Configure(DesktopCloseBehavior closeBehavior)
        => CloseBehavior = closeBehavior;

    public bool ShouldMinimizeToTray()
        => !IsExplicitExitRequested && CloseBehavior == DesktopCloseBehavior.MinimizeToTray;

    public void RequestExplicitExit()
        => Interlocked.Exchange(ref _explicitExitRequested, 1);
}
