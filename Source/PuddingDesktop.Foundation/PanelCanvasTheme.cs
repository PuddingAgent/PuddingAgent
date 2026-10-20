namespace PuddingDesktop.Foundation;

/// <summary>
/// 面板画布沙箱文档的主题契约。
/// <para>
/// **为什么需要它**：Agent 生成的内容无法自己判断宿主是浅色还是深色。
/// WebView2 的 <c>prefers-color-scheme</c> 由 Profile 决定，和宿主外观选择**不是同一件事**
/// （宿主可以在浅色系统上显式选深色）。实测表现是所有画布都渲染成白底，
/// 嵌在深色工具区里像一张白纸贴上去。
/// </para>
/// <para>
/// **契约内容**：壳把 <c>--pudding-*</c> 变量注入 <c>:root</c>，内容用
/// <c>var(--pudding-page)</c> 这类取值，不再自己猜主题；同时声明 <c>color-scheme</c>，
/// 让内容里已有的 <c>@media (prefers-color-scheme)</c> 也与宿主一致。
/// </para>
/// <para>
/// 色值取自可视化规范的克制色板：中性灰承担八成墨水量，蓝图单一高亮。
/// 正文类文字在各自底色上都满足 4.5:1 —— 低对比度的"淡灰提示文字"在投影和
/// 高分屏上会直接消失，所以 <see cref="PanelPalette.TextTertiary"/> 比常见设计稿更深。
/// </para>
/// </summary>
public static class PanelCanvasTheme
{
    /// <summary>浅色画布底色。</summary>
    public const uint LightPageArgb = 0xFFFFFFFF;

    /// <summary>深色画布底色（比工具区略浅一档，让内容与容器分层）。</summary>
    public const uint DarkPageArgb = 0xFF141312;

    /// <summary>画布主题变量集（不含 CSS 语法，便于逐项断言与复用）。</summary>
    public readonly record struct PanelPalette(
        string Page,
        string Surface,
        string SurfaceStrong,
        string Text,
        string TextSecondary,
        string TextTertiary,
        string Border,
        string Accent);

    /// <summary>浅色主题配色。</summary>
    public static readonly PanelPalette Light = new(
        Page: "#FFFFFF",
        Surface: "#F7F6F3",
        SurfaceStrong: "#EDEBE6",
        Text: "#33322E",
        TextSecondary: "#6B6A64",
        TextTertiary: "#757370",
        Border: "#D8D6CF",
        Accent: "#3B6FA8");

    /// <summary>深色主题配色。</summary>
    public static readonly PanelPalette Dark = new(
        Page: "#141312",
        Surface: "#242320",
        SurfaceStrong: "#1B1A18",
        Text: "#E8E6E0",
        TextSecondary: "#A3A19A",
        TextTertiary: "#8A887F",
        Border: "#3A3936",
        Accent: "#6FA0D8");

    /// <summary>生效配色 → 画布配色集。</summary>
    public static PanelPalette For(WorkbenchColorScheme scheme) =>
        scheme == WorkbenchColorScheme.Dark ? Dark : Light;

    /// <summary>生效配色 → 画布预绘制底色（ARGB），用于 WebView2 首帧，避免闪白。</summary>
    public static uint PageArgb(WorkbenchColorScheme scheme) =>
        scheme == WorkbenchColorScheme.Dark ? DarkPageArgb : LightPageArgb;

    /// <summary>生效配色 → CSS <c>color-scheme</c> 取值。</summary>
    public static string ColorSchemeName(WorkbenchColorScheme scheme) =>
        scheme == WorkbenchColorScheme.Dark ? "dark" : "light";

    /// <summary>
    /// 生成注入沙箱壳的 <c>:root</c> 块（含 <c>color-scheme</c> 与全部变量）。
    /// 变量名固定为 <c>--pudding-*</c>：避开宿主与内容自有变量，也便于内容做
    /// <c>var(--pudding-page, #fff)</c> 的失败回退。
    /// </summary>
    public static string Declare(WorkbenchColorScheme scheme)
    {
        var palette = For(scheme);
        return ":root{"
            + "color-scheme:" + ColorSchemeName(scheme) + ";"
            + "--pudding-page:" + palette.Page + ";"
            + "--pudding-surface:" + palette.Surface + ";"
            + "--pudding-surface-strong:" + palette.SurfaceStrong + ";"
            + "--pudding-text:" + palette.Text + ";"
            + "--pudding-text-secondary:" + palette.TextSecondary + ";"
            + "--pudding-text-tertiary:" + palette.TextTertiary + ";"
            + "--pudding-border:" + palette.Border + ";"
            + "--pudding-accent:" + palette.Accent + ";"
            + "}";
    }
}
