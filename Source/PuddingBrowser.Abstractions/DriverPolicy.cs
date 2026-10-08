namespace PuddingBrowser.Abstractions;

/// <summary>
/// **驱动策略注入点**（设计方案 §7/§8）。
///
/// 为什么需要它：驱动源码（<c>WebView2BrowserPage</c> 等）会被 **WinUI 面链接**编译，
/// 而那一面**只允许引用 <c>PuddingBrowser.Abstractions</c>**（`EnforceBrowserBoundary`）。
/// 因此策略（对话框怎么答、事件摘要怎么写……）不能由驱动直接引用策略实现程序集，
/// 只能**由宿主注入**：Abstractions 定义形状，Desktop 侧把 Automation 里的纯策略适配进来。
///
/// **安全默认由驱动保证，策略只提供"怎么答 + 怎么写摘要"**：
/// <list type="bullet">
///   <item>脚本对话框：按 <see cref="AnswerScriptDialog"/> 的答案应答；</item>
///   <item>权限请求：**一律拒绝**、新窗口：**抑制**、下载：**取消**（这三条是驱动的结构选择，不由策略放开）；</item>
///   <item>策略负责给出**有界、无敏感内容**的事实摘要（写进页面事件日志）。</item>
/// </list>
/// 没有注入策略时使用 <see cref="NullBrowserDriverPolicy"/>：安全默认照旧生效，只是摘要更简略。
/// </summary>
public interface IBrowserDriverPolicy
{
    /// <summary>脚本对话框的答案与事实摘要。</summary>
    DriverDialogAnswer AnswerScriptDialog(string kind, string? message);

    /// <summary>权限请求的事实摘要（驱动会拒绝该请求）。</summary>
    string DescribePermissionRequest(string kind, string? uri);

    /// <summary>新窗口请求的事实摘要（驱动会抑制弹窗）。</summary>
    string DescribeWindowRequest(string? uri);

    /// <summary>下载请求的事实摘要（驱动会取消下载）。</summary>
    string DescribeDownloadRequest(string? suggestedFileName, string? uri);

    /// <summary>
    /// 创建一个**页面事件日志**（有界、连续重复合并）。
    /// 返回 <c>null</c> 表示这一面不做事实上报（驱动会如实给 <c>null</c>，不编造）。
    /// </summary>
    IBrowserPageEventLog? CreateEventLog();

    /// <summary>
    /// 原生输入的计划（返回 CDP 调用序列）。返回 <c>null</c> 表示**该动作在此面不支持**，
    /// 驱动会据此明确报 `browser_input_mode_not_supported`（**绝不静默回落成 DOM 输入**）。
    /// </summary>
    IReadOnlyList<DriverCall>? PlanClick(double x, double y, int clickCount);

    IReadOnlyList<DriverCall>? PlanHover(double x, double y);

    /// <summary>
    /// 拖拽计划：从起点到终点的分步移动（中间必须有 mousemove 序列），按下必然抬起。
    /// <c>null</c> 表示该面不支持拖拽 ⇒ 驱动明确 unsupported。
    /// </summary>
    IReadOnlyList<DriverCall>? PlanDrag(double fromX, double fromY, double toX, double toY);

    IReadOnlyList<DriverCall>? PlanScroll(double x, double y, double deltaX, double deltaY);

    IReadOnlyList<DriverCall>? PlanFill(string? text);

    IReadOnlyList<DriverCall>? PlanType(string? text);

    IReadOnlyList<DriverCall>? PlanPress(string? key);

    /// <summary>取消时清理按键状态（把按下过的修饰键逐个抬起）。</summary>
    IReadOnlyList<DriverCall>? ReleaseModifiers(int modifiers);

    /// <summary>
    /// 截图的 CDP 参数（JSON）。<paramref name="fullPage"/> 为真时给出内容尺寸，由策略决定 clip。
    /// 返回 <c>null</c> 表示该面不支持截图 ⇒ 驱动明确 unsupported。
    /// </summary>
    string? BuildScreenshotParameters(
        int? quality, bool fullPage, double contentX, double contentY, double contentWidth, double contentHeight);
}

/// <summary>一次驱动调用：CDP 方法名 + 已序列化的参数。</summary>
public readonly record struct DriverCall(string Method, string ParametersJson);

/// <summary>
/// 页面事件日志（有界、连续重复合并）。<see cref="Entries"/> 给出**可直接输出**的摘要
/// （合并过的要带上次数），内容必须是有界且无敏感信息的。
/// </summary>
public interface IBrowserPageEventLog
{
    /// <summary>追加一条摘要（空白忽略）；与上一条相同则合并计数。</summary>
    void Add(string? summary);

    /// <summary>当前保留的事件（顺序 = 发生顺序，合并项已带次数）。</summary>
    IReadOnlyList<string> Entries { get; }

    /// <summary>是否没有任何事件。</summary>
    bool IsEmpty { get; }
}

/// <summary>脚本对话框的答案：是否接受 + 事实摘要。</summary>
public readonly record struct DriverDialogAnswer(bool Accept, string Summary);

/// <summary>
/// 未注入策略时的**安全默认**：三条结构选择不变（拒绝权限/抑制弹窗/取消下载），
/// 摘要只给种类而不猜测更多内容。它保证"没有策略也不会把页面卡住"。
/// </summary>
public sealed class NullBrowserDriverPolicy : IBrowserDriverPolicy
{
    public static NullBrowserDriverPolicy Instance { get; } = new();

    private NullBrowserDriverPolicy()
    {
    }

    /// <summary>alert 只有一个 OK ⇒ 接受；其余一律取消（肯定可能触发不可逆操作）。</summary>
    public DriverDialogAnswer AnswerScriptDialog(string kind, string? message) =>
        new(
            string.Equals(kind, "Alert", StringComparison.OrdinalIgnoreCase),
            $"scriptDialog kind={Lower(kind)} (no driver policy injected)");

    public string DescribePermissionRequest(string kind, string? uri) =>
        $"permissionRequest kind={Lower(kind)} answered=deny host={HostOf(uri)}".TrimEnd();

    public string DescribeWindowRequest(string? uri) =>
        $"newWindowRequest answered=suppress host={HostOf(uri)} (suppressed)".TrimEnd();

    public string DescribeDownloadRequest(string? suggestedFileName, string? uri) =>
        $"downloadRequest answered=cancel host={HostOf(uri)} (no file was written)".TrimEnd();

    /// <summary>没有策略 ⇒ 不做事实上报（驱动如实给 null，不编造）。</summary>
    public IBrowserPageEventLog? CreateEventLog() => null;

    // 原生输入与截图需要具体实现（Automation 的纯策略/参数语义）；没有策略 ⇒ 明确不支持。
    public IReadOnlyList<DriverCall>? PlanClick(double x, double y, int clickCount) => null;

    public IReadOnlyList<DriverCall>? PlanHover(double x, double y) => null;

    public IReadOnlyList<DriverCall>? PlanDrag(double fromX, double fromY, double toX, double toY) => null;

    public IReadOnlyList<DriverCall>? PlanScroll(double x, double y, double deltaX, double deltaY) => null;

    public IReadOnlyList<DriverCall>? PlanFill(string? text) => null;

    public IReadOnlyList<DriverCall>? PlanType(string? text) => null;

    public IReadOnlyList<DriverCall>? PlanPress(string? key) => null;

    public IReadOnlyList<DriverCall>? ReleaseModifiers(int modifiers) => null;

    public string? BuildScreenshotParameters(
        int? quality, bool fullPage, double contentX, double contentY, double contentWidth, double contentHeight)
        => null;

    private static string Lower(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();

    /// <summary>只留 <c>scheme://host</c>（不回显路径与查询串，避免把 token 带进日志）。</summary>
    private static string HostOf(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.Host)
            ? parsed.IsDefaultPort ? $"{parsed.Scheme}://{parsed.Host}" : $"{parsed.Scheme}://{parsed.Host}:{parsed.Port}"
            : string.Empty;
}
