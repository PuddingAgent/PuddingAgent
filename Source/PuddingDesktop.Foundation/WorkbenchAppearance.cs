namespace PuddingDesktop.Foundation;

/// <summary>
/// Shell 外观选择（对应设置页主题下拉三项）。
/// </summary>
public enum WorkbenchThemePreference
{
    /// <summary>跟随系统（下拉第 0 项；WebView2 保持 UA 自动配色）。</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>生效配色（由外观选择 + 系统实际主题解析得出）。</summary>
public enum WorkbenchColorScheme
{
    Light,
    Dark,
}

/// <summary>
/// 工作台宿主外观映射（设计规格 §14.4 第 1/2 步）。
/// <para>
/// 只负责「外观选择 → 生效配色 → 预绘制背景色」这一层纯判断，不含任何 UI 类型，
/// 因此可在无 UI 边界单测（Foundation 是 BCL-only 叶子，见组件化交付规程）。
/// 宿主真实色值由调用方（MainWindow）落到 WebView2 上。
/// </para>
/// <para>
/// 背景色真源在 Web 侧：<c>src/components/ThemeMode/index.tsx</c> 的 antd
/// <c>colorBgLayout</c>（浅 #F5F0E8 / 深 #0B1020），对应
/// <c>src/global.style.ts</c> 的 <c>--ant-colorBgLayout</c>。
/// WebView2 在首帧之前使用该色，避免网页加载前闪白（SCROLL-001 / IMG02）。
/// 若 Web 侧主题底色调整（如 §3 中性色批次），此处必须同步。
/// </para>
/// </summary>
public static class WorkbenchAppearance
{
    /// <summary>浅色布局背景 #F5F0E8。</summary>
    public const uint LightBackgroundArgb = 0xFFF5F0E8;

    /// <summary>深色布局背景 #0B1020。</summary>
    public const uint DarkBackgroundArgb = 0xFF0B1020;

    /// <summary>把设置里保存的 <c>Theme</c> 字符串解析为外观选择；未知/空值跟随系统。</summary>
    public static WorkbenchThemePreference ParsePreference(string? saved) => saved switch
    {
        "Light" => WorkbenchThemePreference.Light,
        "Dark" => WorkbenchThemePreference.Dark,
        _ => WorkbenchThemePreference.System,
    };

    /// <summary>外观选择 → 主题下拉选中项下标。</summary>
    public static int ComboIndex(WorkbenchThemePreference preference) => (int)preference;

    /// <summary>主题下拉选中项下标 → 外观选择（越界按跟随系统处理）。</summary>
    public static WorkbenchThemePreference PreferenceFromComboIndex(int index) => index switch
    {
        1 => WorkbenchThemePreference.Light,
        2 => WorkbenchThemePreference.Dark,
        _ => WorkbenchThemePreference.System,
    };

    /// <summary>
    /// 解析生效配色：显式浅/深优先；跟随系统时才采用系统实际主题
    /// （<c>systemPrefersDark</c> 应取自 <c>FrameworkElement.ActualTheme</c>）。
    /// </summary>
    public static WorkbenchColorScheme Resolve(WorkbenchThemePreference preference, bool systemPrefersDark) =>
        preference switch
        {
            WorkbenchThemePreference.Light => WorkbenchColorScheme.Light,
            WorkbenchThemePreference.Dark => WorkbenchColorScheme.Dark,
            _ => systemPrefersDark ? WorkbenchColorScheme.Dark : WorkbenchColorScheme.Light,
        };

    /// <summary>生效配色 → WebView2 预绘制背景（ARGB）。</summary>
    public static uint BackgroundArgb(WorkbenchColorScheme scheme) =>
        scheme == WorkbenchColorScheme.Dark ? DarkBackgroundArgb : LightBackgroundArgb;

    /// <summary>ARGB 拆分量（供 UI 层构造颜色，避免在 Foundation 引入 UI 类型）。</summary>
    public static (byte Alpha, byte Red, byte Green, byte Blue) ToArgbParts(uint argb) =>
        ((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
