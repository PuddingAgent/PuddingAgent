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

    /// <summary>
    /// 首次使用（尚无用户保存比例）且只显示工具首页时的推荐比例
    /// （设计规格 §13.3：0.32，可用范围约 30–35%）。
    /// <para>
    /// 只用于「没有用户偏好」的初始化：一旦用户拖动/键盘调整/双击复位过，就必须
    /// 沿用保存值；也**不得**在切换标签页时重算比例，否则会持续布局跳动与文本重排。
    /// </para>
    /// </summary>
    public const double LauncherOnlyWidthRatio = 0.32;

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
    /// 无用户偏好时的初始比例：只显示工具首页（启动器）→ <see cref="LauncherOnlyWidthRatio"/>；
    /// 已经在用真实工具 → <see cref="DefaultWidthRatio"/>。
    /// <para>
    /// 有保存偏好时调用方必须沿用保存值，不要调用本方法 —— 这条边界由调用方保证，
    /// 这里只负责「未配置时用哪个数」这一件事。
    /// </para>
    /// </summary>
    public static double InitialRatioWithoutPreference(bool launcherOnly) =>
        launcherOnly ? LauncherOnlyWidthRatio : DefaultWidthRatio;

    /// <summary>
    /// 双击「恢复默认分栏」的目标比例，按当前上下文取：只有工具首页时回到启动器比例，
    /// 否则回到通用默认。纯函数，便于无 UI 单测。
    /// </summary>
    public static double DefaultRatioFor(bool launcherOnly) => InitialRatioWithoutPreference(launcherOnly);

    /// <summary>
    /// 加载时决定生效比例：**有保存偏好就一律沿用保存值**（哪怕它正好等于通用默认，
    /// 或当前只有工具首页）；只有从未配置过时才用初始化默认。
    /// <para>
    /// 这条是 §13.3 的关键边界：初始化默认只能影响「未配置」状态，不能覆盖用户设置；
    /// 而且本方法只应在加载时调用 —— 切换标签页不得重新计算比例（否则持续抖动）。
    /// </para>
    /// </summary>
    public static double ResolveLoadedRatio(double? savedRatio, bool launcherOnly) =>
        savedRatio is { } saved && double.IsFinite(saved)
            ? Math.Clamp(saved, MinimumWidthRatio, MaximumWidthRatio)
            : InitialRatioWithoutPreference(launcherOnly);

    /// <summary>双击复位到给定默认比例（其它设置不变）。</summary>
    public ToolWorkspaceLayout ResetWidth(double ratio) => this with { WidthRatio = ratio };

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
