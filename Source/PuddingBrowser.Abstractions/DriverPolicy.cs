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

    private static string Lower(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();

    /// <summary>只留 <c>scheme://host</c>（不回显路径与查询串，避免把 token 带进日志）。</summary>
    private static string HostOf(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.Host)
            ? parsed.IsDefaultPort ? $"{parsed.Scheme}://{parsed.Host}" : $"{parsed.Scheme}://{parsed.Host}:{parsed.Port}"
            : string.Empty;
}
