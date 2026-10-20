using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PuddingPlatform.Services;

/// <summary>文档预览路径策略的错误码；控制器按码映射 HTTP 状态。</summary>
public static class DocumentPreviewErrorCodes
{
    /// <summary>路径为空、绝对路径、含 `..`、含控制字符等。</summary>
    public const string InvalidPath = "documents.invalid_path";

    /// <summary>扩展名不在纯文本白名单内。</summary>
    public const string UnsupportedType = "documents.unsupported_type";

    /// <summary>文件不存在，或（出于信息隐藏）落在根目录之外。</summary>
    public const string NotFound = "documents.not_found";

    /// <summary>文件超过单次读取上限。</summary>
    public const string TooLarge = "documents.too_large";

    /// <summary>宿主无法确定文档根目录（部署形态不含仓库标记且未配置环境变量）。</summary>
    public const string RootUnavailable = "documents.root_unavailable";
}

/// <summary>
/// **文档预览的路径安全策略**（纯函数，无 I/O）。
/// <para>
/// 为什么单独抽出来：这条链路唯一的真实风险是"用相对路径读到根目录之外"。
/// 该逻辑若写在控制器里就只能在 HTTP 测试里覆盖，而 HTTP 测试对 traversal 变体最不敏感
/// （不同框架对 `..;`、URL 编码、大小写盘符的处理各不相同）。抽成纯函数后可以逐个变体断言。
/// </para>
/// <para>
/// 四层拒绝，任何一层命中都 fail-closed：绝对路径 → 段级校验 → 扩展名白名单 → 拼全路径后
/// 再做一次"是否仍在根内"的前缀复核（防止符号链接与分隔符边缘情况）。
/// </para>
/// </summary>
public static class DocumentPreviewPathPolicy
{
    /// <summary>单次读取上限。超过就报错而不是截断——截断的文档会被误读成"文件就这么长"。</summary>
    public const int MaxContentBytes = 2 * 1024 * 1024;

    /// <summary>只允许纯文本类。PDF/HTML 需要各自沙箱与渲染器，混进 Markdown 渲染路径只会显示成源码。</summary>
    private static readonly string[] AllowedExtensions = [".md", ".markdown", ".txt"];

    /// <summary>根目录之外的路径一律当作"不存在"，不区分"无权限"与"不存在"，避免探测。</summary>
    public static bool IsSupportedExtension(string relativePath)
    {
        foreach (var ext in AllowedExtensions)
        {
            if (relativePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 把调用方给的相对路径规整成 `a/b/c.md` 形式；任何可疑形态直接失败。
    /// </summary>
    /// <param name="raw">原始查询参数，可能为 null / 空 / 含反斜杠。</param>
    /// <param name="normalized">成功时为正斜杠分隔的相对路径。</param>
    /// <param name="errorCode">失败时为 <see cref="DocumentPreviewErrorCodes"/> 之一。</param>
    /// <param name="errorMessage">失败时的用户可读原因。</param>
    public static bool TryNormalize(
        string? raw,
        [NotNullWhen(true)] out string? normalized,
        out string? errorCode,
        out string? errorMessage)
    {
        normalized = null;
        errorCode = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            errorCode = DocumentPreviewErrorCodes.InvalidPath;
            errorMessage = "path 不能为空";
            return false;
        }

        var text = raw.Trim();

        // 统一分隔符；Windows 客户端可能传反斜杠。
        text = text.Replace('\\', '/');

        // 绝对路径与 UNC：两种都可能绕过根目录。
        if (text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal))
        {
            errorCode = DocumentPreviewErrorCodes.InvalidPath;
            errorMessage = "只接受相对路径，不允许以 / 开头";
            return false;
        }

        // 盘符（`C:`）与任何含冒号的段（备用数据流 `file.md::$DATA`）都拒绝。
        if (text.Contains(':'))
        {
            errorCode = DocumentPreviewErrorCodes.InvalidPath;
            errorMessage = "路径不允许包含盘符或冒号";
            return false;
        }

        if (text.Any(char.IsControl))
        {
            errorCode = DocumentPreviewErrorCodes.InvalidPath;
            errorMessage = "路径包含控制字符";
            return false;
        }

        var segments = text.Split('/');
        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                errorCode = DocumentPreviewErrorCodes.InvalidPath;
                errorMessage = "路径不允许出现空段（重复的 /）";
                return false;
            }

            if (segment is "." or "..")
            {
                errorCode = DocumentPreviewErrorCodes.InvalidPath;
                errorMessage = "路径不允许包含 . 或 .. 段";
                return false;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                // Windows 会静默去掉尾随空格/点，`a.md ` 与 `a.md` 指向同一个文件，
                // 但字符串比较不同 —— 先拒绝，不要制造两套等价写法。
                errorCode = DocumentPreviewErrorCodes.InvalidPath;
                errorMessage = "路径段不允许以空格或点结尾";
                return false;
            }

            kept.Add(segment);
        }

        var joined = string.Join('/', kept);
        if (joined.Length == 0)
        {
            errorCode = DocumentPreviewErrorCodes.InvalidPath;
            errorMessage = "path 不能为空";
            return false;
        }

        if (!IsSupportedExtension(joined))
        {
            errorCode = DocumentPreviewErrorCodes.UnsupportedType;
            errorMessage = $"只支持预览 {string.Join(" / ", AllowedExtensions)} 文件";
            return false;
        }

        normalized = joined;
        return true;
    }

    /// <summary>
    /// 拼出绝对路径并复核它确实落在根目录内。
    /// </summary>
    public static bool TryResolveFullPath(
        string root,
        string normalizedRelativePath,
        [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;

        string rootFull;
        string candidate;
        try
        {
            rootFull = Path.GetFullPath(root);
            candidate = Path.GetFullPath(Path.Combine(rootFull, normalizedRelativePath));
        }
        catch (Exception)
        {
            // 非法字符、路径过长等 —— 当作路径非法处理，不抛给上层。
            return false;
        }

        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        // 大小写不敏感：Windows 盘符与目录名都可能被改写大小写。
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// 解析文档根目录：先看环境变量 <c>PUDDING_DOCS_ROOT</c>，再从程序目录向上找仓库标记。
    /// 两级都失败时返回 null（调用方据此返回 root_unavailable，绝不回退到当前目录）。
    /// </summary>
    public static string? ResolveRoot(Func<string?>? environmentReader = null)
    {
        var configured = environmentReader?.Invoke()
                         ?? Environment.GetEnvironmentVariable("PUDDING_DOCS_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim();
            return Directory.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
        }

        // 本地开发形态：Core 的 deploy 目录位于仓库内，向上能碰到解决方案标记。
        // 独立安装形态下碰不到，于是 fail-closed 而不是拿进程 cwd 当根。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuddingAgentNetwork.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }
}
