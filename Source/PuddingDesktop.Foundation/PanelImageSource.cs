namespace PuddingDesktop.Foundation;

/// <summary>
/// 面板画布的**本地图片**来源：自定义 scheme 加路径白名单。
///
/// <para>
/// 起因（用户 2026-10-20）：「让面板支持加载本地图片（绝对路径），后续会有图片编辑标注需求」。
/// 只有 <c>data:</c> 时，一张 820px 的图就要 70KB 的 base64；4K 照片会到几 MB，
/// 画布明显卡顿，标注还要反复回写同一份内容。
/// </para>
///
/// <para>
/// 为什么不用 <c>file://</c>：<c>NavigateToString</c> 的文档 origin 是 <c>about:blank</c>，
/// file 请求会被 WebView2 拦掉；即便放行，也等于把**任意磁盘读**交给了 Agent 生成的 HTML。
/// 所以走自定义 scheme —— Shell 拦截后按白名单读盘，可审计、可拒绝。
/// </para>
///
/// <para>
/// 本类只做**不碰磁盘**的判定（scheme、路径还原、扩展名、白名单），
/// 存在性 / 大小 / 可读性由调用方在读盘时判定。这样它可以在无窗口、无文件系统的
/// 环境里被完整测试。
/// </para>
/// </summary>
public static class PanelImageSource
{
    /// <summary>面板 HTML 里使用的 scheme 名。</summary>
    public const string Scheme = "pudding-image";

    /// <summary>写进 CSP 的 <c>img-src</c> 来源。</summary>
    public const string ContentSecurityPolicySource = Scheme + ":";

    /// <summary>
    /// 单张图片上限。读盘前先看长度：把一个几 GB 的文件塞进 WebView2 只会让画布假死，
    /// 而且没有任何提示。
    /// </summary>
    public const long MaxImageBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 允许的扩展名。SVG 也在列：它作为 <c>&lt;img&gt;</c> 加载时是隔离上下文，
    /// 内嵌脚本不会执行。
    /// </summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg",
    };

    /// <summary>面板里可直接使用的 src 前缀。</summary>
    public const string UriPrefix = Scheme + ":///";

    /// <summary>
    /// 把本地绝对路径转成面板可用的 src：<c>D:\a\b c.jpg</c> → <c>pudding-image:///D:/a/b%20c.jpg</c>。
    ///
    /// <para>
    /// 借 <see cref="Uri"/> 的 file 形式做规范化与转义，而不是自己拼字符串：
    /// 空格、中文、<c>#</c>、<c>%</c> 都会被正确编码，段与段之间的分隔符保持不变，
    /// 于是 Agent 写出来的 URL 仍然可读。
    /// </para>
    /// </summary>
    public static string BuildUri(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        Uri fileUri;
        try
        {
            fileUri = new Uri(absolutePath, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            // 相对路径在这里抛的是 UriFormatException（FormatException 的子类），
            // 和本类其它参数错误不同类。包成 ArgumentException，让调用方只需处理一种。
            throw new ArgumentException(
                "Path must be an absolute local file path.", nameof(absolutePath), ex);
        }

        if (!fileUri.IsFile)
        {
            throw new ArgumentException(
                "Only local file paths can be exposed to the canvas.", nameof(absolutePath));
        }

        const string filePrefix = "file:///";
        var absolute = fileUri.AbsoluteUri;
        var rest = absolute.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)
            ? absolute[filePrefix.Length..]
            : absolute;

        return UriPrefix + rest;
    }

    /// <summary>
    /// 把画布请求还原成磁盘路径，并过一遍白名单。
    ///
    /// <para>
    /// 白名单是必需的不是保险：没有它，Agent 生成的任意 HTML 都能读整块盘
    /// （<c>pudding-image:///C:/Users/.../id_rsa.png</c> 这种只要扩展名对就放行）。
    /// </para>
    /// </summary>
    public static bool TryResolve(
        string? requestUri,
        IReadOnlyList<string> allowedRoots,
        out string path,
        out PanelImageRejection rejection)
    {
        path = string.Empty;
        rejection = PanelImageRejection.None;

        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            rejection = PanelImageRejection.MalformedUri;
            return false;
        }

        var decoded = Uri.UnescapeDataString(uri.AbsolutePath);
        if (decoded.StartsWith('/'))
        {
            decoded = decoded[1..];
        }

        var candidate = decoded.Replace('/', '\\');
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            rejection = PanelImageRejection.MalformedUri;
            return false;
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(candidate)))
        {
            rejection = PanelImageRejection.UnsupportedExtension;
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejection = PanelImageRejection.MalformedUri;
            return false;
        }

        if (!IsUnderAnyRoot(full, allowedRoots))
        {
            rejection = PanelImageRejection.OutsideAllowedRoots;
            return false;
        }

        path = full;
        return true;
    }

    /// <summary>
    /// 是否落在任一允许根之下。
    ///
    /// <para>
    /// 必须按**路径段**比较，不能直接 <c>StartsWith</c>：<c>D:\Data2\x</c> 以
    /// <c>D:\Data</c> 开头，字符串前缀会把它当成合法路径。
    /// 补一个分隔符再比，就变成「是 D:\Data\ 之下」而不是「以 D:\Data 开头」。
    /// </para>
    /// </summary>
    private static bool IsUnderAnyRoot(string fullPath, IReadOnlyList<string> allowedRoots)
    {
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            {
                normalizedRoot += Path.DirectorySeparatorChar;
            }

            if (fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>为什么这张图片没被放行。调用方据此回 HTTP 状态并在需要时记日志。</summary>
public enum PanelImageRejection
{
    /// <summary>通过。</summary>
    None = 0,

    /// <summary>不是 <c>pudding-image://</c>，或路径无法还原成绝对路径。</summary>
    MalformedUri,

    /// <summary>扩展名不在允许列表内（挡住「只要后缀改成 png 就能读」的路子）。</summary>
    UnsupportedExtension,

    /// <summary>解析后的真实路径不在任何允许的根目录下。</summary>
    OutsideAllowedRoots,
}
