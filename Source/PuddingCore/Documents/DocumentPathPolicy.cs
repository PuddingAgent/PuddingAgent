using System.Diagnostics.CodeAnalysis;

namespace PuddingCore.Documents;

/// <summary>
/// 文档预览路径策略的**共享错误码**。
/// <para>
/// 字符串值必须与 `PuddingPlatform.Services.DocumentPreviewErrorCodes` 逐字一致：
/// HTTP 状态映射与前端文案都按这些字面量匹配，改一个字母就会让两个调用方分叉。
/// </para>
/// </summary>
public static class DocumentPathErrors
{
    /// <summary>路径为空、绝对路径、含 `..`、含控制字符等。</summary>
    public const string InvalidPath = "documents.invalid_path";

    /// <summary>扩展名不在纯文本白名单内。</summary>
    public const string UnsupportedType = "documents.unsupported_type";

    /// <summary>文件不存在，或（出于信息隐藏）落在根目录之外。</summary>
    public const string NotFound = "documents.not_found";

    /// <summary>文件超过单次读取上限。</summary>
    public const string TooLarge = "documents.too_large";

    /// <summary>无法确定文档根目录（部署形态不含仓库标记且未配置环境变量）。</summary>
    public const string RootUnavailable = "documents.root_unavailable";
}

/// <summary>
/// 文档相对路径的规整与解析（**纯函数，无 I/O**）。
///
/// <para>
/// **为什么放在 PuddingCore 而不是 PuddingPlatform**：同一条规则被两个执行体需要 ——
/// Core 的 HTTP 接口（发送/校验前）与 Desktop 的文档读取（打开文件前）。Desktop 的
/// ProjectReference 有编译期白名单（PuddingDesktop.csproj 的 _ForbiddenShellReference），
/// 而 PuddingCore 是双方都已引用的最底层，因此这里是唯一"不需要新增引用、也不需要
/// 复制第二份规则"的落点。
/// </para>
/// <para>
/// 规则本身一条都不重写：逐字对应 PuddingPlatform 现有实现，后者改为委托到本类。
/// 这样两份真源的问题从**编译期**消失 —— Desktop 想用错的规则也拿不到。
/// </para>
/// <para>
/// 四层拒绝，任何一层命中都 fail-closed：绝对路径 → 段级校验 → 扩展名白名单 →
/// 拼全路径后再复核"是否仍在根内"（防符号链接与分隔符边缘情况）。
/// </para>
/// </summary>
public static class DocumentPathPolicy
{
    /// <summary>单次读取上限。超过就报错而不是截断——截断的文档会被误读成"文件就这么长"。</summary>
    public const int MaxContentBytes = 2 * 1024 * 1024;

    /// <summary>只允许纯文本类。PDF/HTML 需要各自沙箱与渲染器，混进 Markdown 渲染路径只会显示成源码。</summary>
    private static readonly string[] AllowedExtensions = [".md", ".markdown", ".txt"];

    /// <summary>扩展名是否在预览白名单内。</summary>
    public static bool IsSupportedExtension(string relativePath)
    {
        foreach (var ext in AllowedExtensions)
        {
            if (relativePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>支持的扩展名列表（供提示文案使用）。</summary>
    public static IReadOnlyList<string> SupportedExtensions => AllowedExtensions;

    /// <summary>
    /// 把调用方给的相对路径规整成 `a/b/c.md` 形式；任何可疑形态直接失败。
    /// </summary>
    /// <param name="raw">原始输入，可能为 null / 空 / 含反斜杠。</param>
    /// <param name="normalized">成功时为正斜杠分隔的相对路径。</param>
    /// <param name="errorCode">失败时为 <see cref="DocumentPathErrors"/> 之一。</param>
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
            errorCode = DocumentPathErrors.InvalidPath;
            errorMessage = "path 不能为空";
            return false;
        }

        var text = raw.Trim();

        // 统一分隔符；Windows 客户端可能传反斜杠。
        text = text.Replace('\\', '/');

        // 绝对路径与 UNC：两种都可能绕过根目录。
        if (text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal))
        {
            errorCode = DocumentPathErrors.InvalidPath;
            errorMessage = "只接受相对路径，不允许以 / 开头";
            return false;
        }

        // 盘符（`C:`）与任何含冒号的段（备用数据流 `file.md::$DATA`）都拒绝。
        if (text.Contains(':'))
        {
            errorCode = DocumentPathErrors.InvalidPath;
            errorMessage = "路径不允许包含盘符或冒号";
            return false;
        }

        if (text.Any(char.IsControl))
        {
            errorCode = DocumentPathErrors.InvalidPath;
            errorMessage = "路径包含控制字符";
            return false;
        }

        var segments = text.Split('/');
        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                errorCode = DocumentPathErrors.InvalidPath;
                errorMessage = "路径不允许出现空段（重复的 /）";
                return false;
            }

            if (segment is "." or "..")
            {
                errorCode = DocumentPathErrors.InvalidPath;
                errorMessage = "路径不允许包含 . 或 .. 段";
                return false;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                // Windows 会静默去掉尾随空格/点：`a.md ` 与 `a.md` 指向同一个文件，
                // 但字符串比较不同 —— 先拒绝，不要制造两套等价写法。
                errorCode = DocumentPathErrors.InvalidPath;
                errorMessage = "路径段不允许以空格或点结尾";
                return false;
            }

            kept.Add(segment);
        }

        var joined = string.Join('/', kept);
        if (joined.Length == 0)
        {
            errorCode = DocumentPathErrors.InvalidPath;
            errorMessage = "path 不能为空";
            return false;
        }

        if (!IsSupportedExtension(joined))
        {
            errorCode = DocumentPathErrors.UnsupportedType;
            errorMessage = $"只支持预览 {string.Join(" / ", AllowedExtensions)} 文件";
            return false;
        }

        normalized = joined;
        return true;
    }

    /// <summary>
    /// 拼出绝对路径并复核它确实落在根目录内。
    /// <para>
    /// 这一步**只有读盘方才能做**：根目录是执行体的本地配置，Core 与 Desktop 各自
    /// 持有自己的根。所以它被单独暴露，而不是塞进 TryNormalize。
    /// </para>
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

        // 必须带分隔符比较：裸前缀会让 `…\Docs` 放行 `…\DocsEvil\a.md`。
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        // 大小写不敏感：Windows 盘符与目录名都可能被改写大小写。
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        return true;
    }
}
