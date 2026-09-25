using System.Security.Cryptography;
using System.Text;

namespace PuddingFullTextIndex.Cli;

/// <summary>
/// 组件内部口径的<b>镜像</b>（跨程序集不可见，故在 CLI 侧逐字复刻）。
/// <para>
/// 复刻对象（A1 已交付；本 CLI 不得改组件，也无法引用 internal）：
/// <list type="number">
/// <item><description><c>SupplyScopeNormalizer.TrimTrailingSeparators</c> + <c>ToScopeKey</c>
/// （绝对化 → 去尾分隔符但保住盘根 → 分隔符统一 <c>\</c> → 不变文化小写）。</description></item>
/// <item><description><c>LuceneSearchEngine.GetIndexDirectoryPath</c>
/// （<c>SHA256(GetFullPath(path).TrimEnd(sep).ToUpperInvariant())</c> 小写 hex，拼到索引根下）。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 这是本 CLI 唯一与组件实现耦合的点：口径一旦漂移，<c>status</c> 会报告错误的 scopeKey / 索引目录。
/// 因此由**真实构建的交叉断言**守护（见 <c>SupplyCliBuildTests</c>）：
/// ① <c>build --json</c> 返回的 <c>scopes[0].scopeKey</c> 来自协调器内部规范化，必须与镜像一致；
/// ② 真 Lucene 构建后，镜像解析出的索引目录必须**就是**磁盘上引擎实际建出的那个目录。
/// </para>
/// </summary>
internal static class SupplyScopeMirror
{
    /// <summary>绝对化 + 去尾分隔符（保住盘根 <c>C:\</c>）。</summary>
    internal static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
            return full;

        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && trimmed.Length < root.Length)
            return root;

        return trimmed;
    }

    /// <summary>规范键：分隔符统一为 <c>\</c> 后做不变文化小写（Windows 文件系统大小写不敏感）。</summary>
    internal static string ToScopeKey(string normalizedPath) =>
        normalizedPath.Replace('/', '\\').ToLowerInvariant();

    /// <summary>索引目录：<c>&lt;indexRoot&gt;\&lt;sha256(规范化并大写的绝对路径)&gt;</c>（与引擎同口径）。</summary>
    internal static string ResolveIndexDirectory(string indexRoot, string scopePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopePath);

        var normalized = Path.GetFullPath(scopePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(indexRoot, hash);
    }
}
