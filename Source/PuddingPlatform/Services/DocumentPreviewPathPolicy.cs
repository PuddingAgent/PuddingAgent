using System.Diagnostics.CodeAnalysis;
using PuddingCore.Documents;

namespace PuddingPlatform.Services;

/// <summary>
/// 文档预览路径策略的错误码；控制器按码映射 HTTP 状态。
/// <para>
/// 值一律转发到 <see cref="DocumentPathErrors"/>：真正的字面量只有一处，
/// 避免 Core 与 Desktop 两个执行体对同一个码写出不同拼写。
/// </para>
/// </summary>
public static class DocumentPreviewErrorCodes
{
    /// <summary>路径为空、绝对路径、含 `..`、含控制字符等。</summary>
    public const string InvalidPath = DocumentPathErrors.InvalidPath;

    /// <summary>扩展名不在纯文本白名单内。</summary>
    public const string UnsupportedType = DocumentPathErrors.UnsupportedType;

    /// <summary>文件不存在，或（出于信息隐藏）落在根目录之外。</summary>
    public const string NotFound = DocumentPathErrors.NotFound;

    /// <summary>文件超过单次读取上限。</summary>
    public const string TooLarge = DocumentPathErrors.TooLarge;

    /// <summary>宿主无法确定文档根目录（部署形态不含仓库标记且未配置环境变量）。</summary>
    public const string RootUnavailable = DocumentPathErrors.RootUnavailable;
}

/// <summary>
/// 文档预览的路径安全策略（**HTTP 侧的薄封装**）。
/// <para>
/// 真正的规则在 <see cref="DocumentPathPolicy"/>（PuddingCore，纯函数、无 I/O）。
/// 这里存在的唯一理由有两个：
/// 1. 保留既有类型名与命名空间，控制器与 <c>DocumentPreviewPathPolicyTests</c> 不必改写；
/// 2. 承载 Core 特有的**根目录解析**（环境变量 / 向上找仓库标记）—— 那是 I/O，
///    而且 Desktop 不适用（它用自己配置的根），不该被共享出去。
/// </para>
/// <para>
/// 不要在下面重新写校验逻辑：一旦这里出现第二份规则，两个执行体就必然漂移。
/// </para>
/// </summary>
public static class DocumentPreviewPathPolicy
{
    /// <summary>单次读取上限；与共享策略同源。</summary>
    public const int MaxContentBytes = DocumentPathPolicy.MaxContentBytes;

    /// <summary>扩展名是否在预览白名单内。</summary>
    public static bool IsSupportedExtension(string relativePath) =>
        DocumentPathPolicy.IsSupportedExtension(relativePath);

    /// <summary>把调用方给的相对路径规整成 `a/b/c.md` 形式；任何可疑形态直接失败。</summary>
    public static bool TryNormalize(
        string? raw,
        [NotNullWhen(true)] out string? normalized,
        out string? errorCode,
        out string? errorMessage) =>
        DocumentPathPolicy.TryNormalize(raw, out normalized, out errorCode, out errorMessage);

    /// <summary>拼出绝对路径并复核它确实落在根目录内。</summary>
    public static bool TryResolveFullPath(
        string root,
        string normalizedRelativePath,
        [NotNullWhen(true)] out string? fullPath) =>
        DocumentPathPolicy.TryResolveFullPath(root, normalizedRelativePath, out fullPath);

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
