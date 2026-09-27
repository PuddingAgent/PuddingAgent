namespace PuddingDesktop.Foundation;

public sealed record ShellLayout(double NavigationWidth = 248, double WorkspaceWidth = 520,
    bool NavigationVisible = true, bool WorkspaceVisible = true)
{
    public ShellLayout Normalize() => this with
    {
        NavigationWidth = double.IsFinite(NavigationWidth) ? Math.Clamp(NavigationWidth, 200, 320) : 248,
        WorkspaceWidth = double.IsFinite(WorkspaceWidth) ? Math.Clamp(WorkspaceWidth, 360, 1000) : 520
    };

    public LayoutAllocation Allocate(double availableWidth)
    {
        var settings = Normalize();
        var width = double.IsFinite(availableWidth) ? Math.Max(0, availableWidth) : 0;
        var navigation = settings.NavigationVisible ? settings.NavigationWidth : 0;
        var workspace = settings.WorkspaceVisible ? Math.Min(settings.WorkspaceWidth, width * .44) : 0;
        // Keep the editor usable. Auto collapse does not mutate the user's saved preference.
        if (width - navigation - workspace < 480) navigation = 0;
        if (workspace < 360 || width - workspace < 480) workspace = 0;
        return new(navigation, Math.Max(0, width - navigation - workspace), workspace);
    }
}

public sealed record LayoutAllocation(double NavigationWidth, double WorkbenchWidth, double WorkspaceWidth);
