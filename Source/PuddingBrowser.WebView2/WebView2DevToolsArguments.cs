using System.Globalization;

namespace PuddingBrowser.WebView2;

/// <summary>
/// 构造 WebView2 的 <c>AdditionalBrowserArguments</c>，用于按需开启 DevTools（CDP）端点。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要这个开关：WebView2 由 Shell 持有，Chromium 默认**不**监听 DevTools 端口。
/// 不开端口时，外部工具（Playwright <c>ConnectOverCDPAsync</c>、chrome-devtools MCP 等）
/// 无法附着到已经跑起来的、已登录的页面，只能让它们自己 launch 一个新浏览器——
/// 那样就没有用户的登录态，也没有用户正看着的那一页。
/// </para>
/// <para>
/// 安全边界：端点只绑定回环地址，但**同机的任意本地进程**都能通过它驱动这个浏览器。
/// 默认开启（端口 <see cref="DefaultPort"/>）；要关闭，把 <see cref="PortEnvironmentVariable"/> 设为
/// <c>off</c> 后重启 Shell。
/// </para>
/// <para>
/// 端口取值：<c>0</c> 表示由 Chromium 自行挑选空闲端口（实际端口写入用户数据目录下的
/// <c>DevToolsActivePort</c> 文件首行）；<c>1~65535</c> 表示指定端口。
/// </para>
/// </remarks>
public static class WebView2DevToolsArguments
{
    /// <summary>覆盖端口的环境变量名。空字符串或 <c>off</c> / <c>false</c> / <c>0=off</c> 语义见 <see cref="ResolvePort"/>。</summary>
    public const string PortEnvironmentVariable = "PUDDING_BROWSER_CDP_PORT";

    /// <summary>环境变量里表示「显式关闭」的取值。</summary>
    public const string DisableToken = "off";

    /// <summary>默认端口：未配置时自动开启（Shell 持有 WebView2，默认就绪比事后配开关少一轮迷路）。</summary>
    public const int DefaultPort = 9222;

    /// <summary>
    /// 从环境变量解析 CDP 端口；未设置时返回 <see cref="DefaultPort"/>，显式关闭时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 不合法取值回落为默认值而不是抛异常：一个写错的诊断开关不该让整个 Shell 起不来。
    /// 要关闭用 <see cref="DisableToken"/>。
    /// </remarks>
    public static int? ResolvePort(string? rawValue = null)
    {
        var raw = (rawValue ?? Environment.GetEnvironmentVariable(PortEnvironmentVariable))?.Trim();
        if (string.IsNullOrEmpty(raw)) return DefaultPort;
        if (string.Equals(raw, DisableToken, StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) return DefaultPort;
        return port is >= 0 and <= 65535 ? port : DefaultPort;
    }

    /// <summary>
    /// 按端口生成 AdditionalBrowserArguments；<paramref name="port"/> 为 <c>null</c> 时返回 <c>null</c>（不追加任何参数）。
    /// </summary>
    public static string? ForPort(int? port) =>
        port is null ? null : string.Format(CultureInfo.InvariantCulture, "--remote-debugging-port={0}", port.Value);

    /// <summary>组合入口：解析环境变量并生成参数串。</summary>
    public static string? FromEnvironment(string? rawValue = null) => ForPort(ResolvePort(rawValue));
}
