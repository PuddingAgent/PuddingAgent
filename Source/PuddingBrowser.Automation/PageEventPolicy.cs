namespace PuddingBrowser.Automation;

/// <summary>
/// **页面周边事件**的确定性处置策略（设计方案 §8 的可靠性要求）。
///
/// 与 <see cref="ScriptDialogPolicy"/> 同一类问题：这些事件**不处理时 WebView2 会弹 UI 或等人的操作**
/// （权限询问、新窗口、下载另存为），页面在自动化流程里就会"停在那里不动"——而且往往没有明确报错。
///
/// 三条策略都是**安全侧的确定性默认**，并把事实有界地记下来：
/// <list type="bullet">
///   <item><b>权限请求</b>（摄像头/麦克风/定位/通知/剪贴板…）：**一律拒绝**。
///         自动化流程不该因为"页面想要"就悄悄拿到设备权限；要授权必须是显式的产品决定，不是默认。</item>
///   <item><b>新窗口</b>（<c>window.open</c>/target=_blank）：**抑制**并把目标地址记下来。
///         不偷偷改成"在当前页打开"——那会丢掉当前页面状态，是调用方没要求的语义改变。</item>
///   <item><b>下载</b>：**取消**并把建议文件名记下来。文件落盘属于需要授权与去向的副作用，
///         不能让一个页面自己决定往哪儿写。</item>
/// </list>
/// </summary>
public static class PageEventPolicy
{
    /// <summary>权限请求的处置结果（当前策略下恒为拒绝，保留枚举是为了调用方/回执不必靠布尔猜语义）。</summary>
    public enum PermissionVerdict
    {
        Deny,
    }

    /// <summary>新窗口的处置结果。</summary>
    public enum WindowVerdict
    {
        /// <summary>抑制弹窗（不新建窗口、不改动当前页面）。</summary>
        Suppress,
    }

    /// <summary>下载的处置结果。</summary>
    public enum DownloadVerdict
    {
        /// <summary>取消下载（不落盘）。</summary>
        Cancel,
    }

    /// <summary>
    /// 权限请求：**一律拒绝**。
    /// 这里刻意不留"按种类放行"的隐藏后门——真要放行某类权限，应该是一次显式的产品决定
    /// （并伴随调用方可见的授权记录），而不是策略函数里的一个白名单。
    /// </summary>
    public static PermissionVerdict DecidePermission(string? permissionKind) => PermissionVerdict.Deny;

    /// <summary>新窗口：抑制。</summary>
    public static WindowVerdict DecideWindow() => WindowVerdict.Suppress;

    /// <summary>下载：取消。</summary>
    public static DownloadVerdict DecideDownload() => DownloadVerdict.Cancel;

    /// <summary>权限请求的有界事实摘要（种类 + 来源站点，不含页面正文）。</summary>
    public static string DescribePermission(string? permissionKind, string? uri, PermissionVerdict verdict) =>
        $"permissionRequest kind={Kind(permissionKind)} answered={verdict.ToString().ToLowerInvariant()}"
        + OptionalHost(uri);

    /// <summary>新窗口的有界事实摘要（目标地址；被抑制这一点必须写清楚）。</summary>
    public static string DescribeWindow(string? uri, WindowVerdict verdict) =>
        $"newWindowRequest answered={verdict.ToString().ToLowerInvariant()}"
        + OptionalHost(uri)
        + " (the popup was suppressed; navigate explicitly if the target matters)";

    /// <summary>下载的有界事实摘要（建议文件名；被取消这一点必须写清楚）。</summary>
    public static string DescribeDownload(string? suggestedFileName, string? uri, DownloadVerdict verdict) =>
        $"downloadRequest answered={verdict.ToString().ToLowerInvariant()}"
        + OptionalHost(uri)
        + Optional("suggestedName", suggestedFileName)
        + " (no file was written)";

    /// <summary>把地址压成"scheme://host"（不回显完整 URL 的路径与查询串，避免把 token 带进日志）。</summary>
    public static string HostOf(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return string.Empty;
        }

        return parsed.IsDefaultPort
            ? $"{parsed.Scheme}://{parsed.Host}"
            : $"{parsed.Scheme}://{parsed.Host}:{parsed.Port}";
    }

    private static string Kind(string? permissionKind) =>
        string.IsNullOrWhiteSpace(permissionKind) ? "unknown" : permissionKind.Trim().ToLowerInvariant();

    private static string OptionalHost(string? uri)
    {
        var host = HostOf(uri);
        return host.Length == 0 ? string.Empty : $" host={host}";
    }

    private static string Optional(string name, string? value)
    {
        var normalized = ScriptDialogPolicy.Normalize(value);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return normalized.Length > 80 ? $" {name}={normalized[..80]}…" : $" {name}={normalized}";
    }
}
