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
/// 宿主希望 WebView2 采用的 UA 配色（调用方映射到 <c>CoreWebView2PreferredColorScheme</c>）。
/// <para>
/// 在 Foundation 用自有枚举而不是直接引用 WebView2 类型：这里是纯判断、可无 UI 单测，
/// 也避免 BCL-only 叶子引入 UI 依赖（见组件化交付规程）。
/// </para>
/// </summary>
public enum WorkbenchPreferredColorScheme
{
    /// <summary>跟随系统：**必须**映射为 UA 的 <c>Auto</c>，不能把宿主选择冒充成系统偏好。</summary>
    System,
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
/// <c>colorBgLayout</c>（浅 #F7F8FA / 深 #11151B，IMG01 后为 §3 中性色），
/// 对应 <c>src/global.style.ts</c> 的 <c>--ant-colorBgLayout</c>。
/// WebView2 在首帧之前使用该色，避免网页加载前闪白（SCROLL-001 / IMG02）。
/// 若 Web 侧主题底色调整，此处必须同步（§14.6 待复核项 2）。
/// </para>
/// </summary>
public static class WorkbenchAppearance
{
    /// <summary>浅色布局背景 #F7F8FA（= §3 bg）。</summary>
    public const uint LightBackgroundArgb = 0xFFF7F8FA;

    /// <summary>深色布局背景 #11151B（= §3 bg）。</summary>
    public const uint DarkBackgroundArgb = 0xFF11151B;

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

    /// <summary>
    /// 外观选择 → 希望 UA 采用的配色。**每个 WebView2 表面都要用同一个结果**
    /// （主工作台与工具区里的浏览器页面），否则浅色应用里会露出一块纯黑画布：
    /// 浏览器页面走 UA 深色时，<c>about:blank</c> 的画布是 Chromium 的 #121212。
    /// </summary>
    public static WorkbenchPreferredColorScheme PreferredColorSchemeFor(WorkbenchThemePreference preference) =>
        preference switch
        {
            WorkbenchThemePreference.Light => WorkbenchPreferredColorScheme.Light,
            WorkbenchThemePreference.Dark => WorkbenchPreferredColorScheme.Dark,
            _ => WorkbenchPreferredColorScheme.System,
        };

    /// <summary>
    /// 标题栏按钮字形（ARGB）。
    /// <para>
    /// 窗口使用 <c>ExtendsContentIntoTitleBar</c> 后，最小化/最大化/关闭**不再自动跟随
    /// 应用主题**——系统深色 + 应用浅色时会留下白色字形画在浅色标题栏上，实测不可分辨。
    /// 所以由宿主外观这里显式给出两套值。
    /// </para>
    /// </summary>
    public static uint CaptionGlyphArgb(WorkbenchColorScheme scheme) =>
        scheme == WorkbenchColorScheme.Dark ? 0xFFFFFFFFu : 0xFF1F1F1Fu;

    /// <summary>标题栏按钮非激活字形（弱化，但仍要看得见）。</summary>
    public static uint CaptionInactiveGlyphArgb(WorkbenchColorScheme scheme) =>
        scheme == WorkbenchColorScheme.Dark ? 0x99FFFFFFu : 0x991F1F1Fu;

    /// <summary>标题栏按钮 hover 背景（半透明叠加，让 Mica 透出来）。</summary>
    public static uint CaptionHoverBackgroundArgb(WorkbenchColorScheme scheme) =>
        scheme == WorkbenchColorScheme.Dark ? 0x1AFFFFFFu : 0x14000000u;

    /// <summary>标题栏按钮按下背景（比 hover 略重）。</summary>
    public static uint CaptionPressedBackgroundArgb(WorkbenchColorScheme scheme) =>
        scheme == WorkbenchColorScheme.Dark ? 0x33FFFFFFu : 0x22000000u;

    /// <summary>ARGB 拆分量（供 UI 层构造颜色，避免在 Foundation 引入 UI 类型）。</summary>
    public static (byte Alpha, byte Red, byte Green, byte Blue) ToArgbParts(uint argb) =>
        ((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
