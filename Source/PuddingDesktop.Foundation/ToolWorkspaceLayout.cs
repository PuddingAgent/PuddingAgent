using System;

namespace PuddingDesktop.Foundation;

/// <summary>
/// Persisted layout preference for the right-hand multi-tab tool workspace.
/// The persisted primitive is the width ratio, not an absolute pixel width, so every
/// expand re-clamps the panel against the current window instead of restoring a width
/// captured on a different monitor or DPI.
/// </summary>
public sealed record ToolWorkspaceLayout
{
    /// <summary>Initial share of the combined chat + tool width (design: about 45%).</summary>
    public const double DefaultWidthRatio = 0.45;

    /// <summary>Minimum usable tool region; below this the panel is not worth opening.</summary>
    public const double MinimumToolWidth = 360;

    /// <summary>Chat keeps at least this width; otherwise the tool panel overlays it.</summary>
    public const double MinimumChatWidth = 560;

    /// <summary>Draggable divider hit area (visual line stays about 1 pixel).</summary>
    public const double SplitterWidth = 6;

    public const double MinimumWidthRatio = 0.1;
    public const double MaximumWidthRatio = 0.9;

    public double WidthRatio { get; init; } = DefaultWidthRatio;

    /// <summary>Off by default: activity only raises a marker unless the user opted in.</summary>
    public bool AutoExpandOnActivity { get; init; }

    public ToolWorkspaceLayout Normalize() => this with
    {
        WidthRatio = double.IsFinite(WidthRatio)
            ? Math.Clamp(WidthRatio, MinimumWidthRatio, MaximumWidthRatio)
            : DefaultWidthRatio
    };

    /// <summary>Double-clicking the divider returns to the default ratio.</summary>
    public ToolWorkspaceLayout ResetWidth() => this with { WidthRatio = DefaultWidthRatio };

    /// <summary>
    /// Records a dragged region width. The stored ratio always corresponds to a visible
    /// width on the current window; the chat/overlay decision stays in <see cref="Resolve"/>.
    /// </summary>
    public ToolWorkspaceLayout WithToolWidth(double availableWidth, double toolWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0 || !double.IsFinite(toolWidth))
            return Normalize();
        var clamped = Math.Clamp(toolWidth, Math.Min(MinimumToolWidth, availableWidth), availableWidth);
        return (this with { WidthRatio = clamped / availableWidth }).Normalize();
    }

    /// <summary>
    /// Resolves the visible split. A narrow window yields an overlay instead of squeezing
    /// the chat below <see cref="MinimumChatWidth"/>; a zoomed workspace takes everything.
    /// </summary>
    public ToolWorkspaceAllocation Resolve(double availableWidth, bool expanded, bool zoomed = false)
    {
        var width = double.IsFinite(availableWidth) ? Math.Max(0, availableWidth) : 0;
        if (!expanded) return new(false, width, 0);
        if (zoomed) return new(true, 0, width);
        if (width <= 0) return new(false, 0, 0);

        // Below the combined minimum the panel covers the chat instead of squeezing it.
        if (width < MinimumToolWidth + MinimumChatWidth)
        {
            // On a window narrower than the tool minimum the panel simply takes what exists.
            var floor = Math.Min(MinimumToolWidth, width);
            return new(true, width, Math.Clamp(width * Normalize().WidthRatio, floor, width));
        }

        // A stored or dragged ratio never pushes the chat below its own minimum.
        var desired = Math.Clamp(width * Normalize().WidthRatio, MinimumToolWidth, width - MinimumChatWidth);
        return new(false, width - desired, desired);
    }
}

/// <summary>
/// Result of <see cref="ToolWorkspaceLayout.Resolve"/>.
/// <paramref name="ToolRegionWidth"/> includes the divider, so chat + tool always
/// adds up to the available width while split.
/// </summary>
public sealed record ToolWorkspaceAllocation(bool SpansContent, double ChatWidth, double ToolRegionWidth)
{
    public bool IsVisible => ToolRegionWidth > 0;

    /// <summary>Width the panel content actually gets; the divider lives beside it.</summary>
    public double PanelWidth => SpansContent
        ? ToolRegionWidth
        : Math.Max(0, ToolRegionWidth - ToolWorkspaceLayout.SplitterWidth);
}
