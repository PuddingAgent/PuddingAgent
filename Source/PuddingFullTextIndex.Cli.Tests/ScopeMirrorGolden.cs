using System.Security.Cryptography;
using System.Text;

namespace PuddingFullTextIndex.Cli.Tests;

/// <summary>
/// **冻结的**旧 CLI 镜像（A19 之前 <c>Source/PuddingFullTextIndex.Cli/SupplyScopeMirror.cs</c> 的逐字副本）。
/// <para>
/// 为什么把它搬进测试工程而不是直接删：A19 的核心验收之一是「**改动前后 CLI 打印的
/// <c>scopeKey</c> / <c>indexDirectory</c> 逐字不变**」，而"改动前"的值只能由一个
/// **不随生产代码变化**的副本给出 —— 它在本测试工程里充当独立金标准（oracle），而不是被测对象。
/// 生产侧已删除命名哈希的复刻，改调组件单一真源
/// <c>FullTextIndexPaths.ResolveIndexDirectory</c>。
/// </para>
/// <para>
/// ⚠️ 本文件是 <c>A19SingleSourceTests.A4_...</c> 的**豁免对象**：A4 的扫描范围是**生产工程**
/// <c>Source/PuddingFullTextIndex.Cli</c>（必须 0 命中）；本文件位于测试工程，且 A4 反向断言
/// 这几段复刻确实仍在这里（防止 A1 退化成"自己和自己比"的同义反复）。
/// </para>
/// <para>
/// ⚠️ 本副本**冻结**：任何"顺手同步成组件口径"的改动都会让金标准与生产同源，
/// 从而让 A1 / A2 失去检测力。
/// </para>
/// </summary>
internal static class ScopeMirrorGolden
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
