using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// scope 规范化 + 逐条拒绝（协调器的第一道门）。
/// <para>
/// 规范化：绝对化（<see cref="Path.GetFullPath(string)"/>，顺带解析 <c>..</c> 与分隔符）→ 去尾分隔符（保留盘根 <c>C:\</c>）
/// → 生成规范键（分隔符统一 <c>\</c> + 不变文化小写，Windows 优先：同一目录只允许一套租约/一条 job）。
/// </para>
/// <para>
/// 拒绝：空 / 相对 / 不存在 / 非目录 / 规范化后重复 / 与已接受 scope 父子嵌套（任一方向）。
/// 拒绝项一律带「值 + 原因枚举 + 可读消息」，不静默丢弃。
/// </para>
/// </summary>
internal static class SupplyScopeNormalizer
{
    /// <summary>规范化结果：接受集 + 拒绝集（两者都返回，调用方不得只看一半）。</summary>
    internal sealed record NormalizationResult(
        IReadOnlyList<SupplyScope> Accepted,
        IReadOnlyList<SupplyScopeRejection> Rejections);

    internal static NormalizationResult Normalize(IReadOnlyList<string>? rootPaths)
    {
        var accepted = new List<SupplyScope>();
        var rejections = new List<SupplyScopeRejection>();

        if (rootPaths is null || rootPaths.Count == 0)
        {
            rejections.Add(new SupplyScopeRejection(
                string.Empty,
                SupplyRejectionReason.Empty,
                "供给请求未包含任何 scope 路径。"));
            return new NormalizationResult(accepted, rejections);
        }

        foreach (var raw in rootPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                rejections.Add(new SupplyScopeRejection(
                    raw ?? string.Empty,
                    SupplyRejectionReason.Empty,
                    "scope 路径为空或仅含空白字符。"));
                continue;
            }

            var trimmed = raw.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                rejections.Add(new SupplyScopeRejection(
                    raw,
                    SupplyRejectionReason.Relative,
                    $"scope 必须是绝对路径（不接受相对路径，也不接受盘符相对形式）：{raw}"));
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(trimmed);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                rejections.Add(new SupplyScopeRejection(
                    raw,
                    SupplyRejectionReason.Relative,
                    $"scope 路径无法规范化（{ex.GetType().Name}）：{raw}"));
                continue;
            }

            var normalized = TrimTrailingSeparators(full);

            if (File.Exists(normalized))
            {
                rejections.Add(new SupplyScopeRejection(
                    normalized,
                    SupplyRejectionReason.NotDirectory,
                    $"scope 指向一个文件而不是目录：{normalized}"));
                continue;
            }

            if (!Directory.Exists(normalized))
            {
                rejections.Add(new SupplyScopeRejection(
                    normalized,
                    SupplyRejectionReason.NotFound,
                    $"scope 目录不存在：{normalized}"));
                continue;
            }

            var scopeKey = ToScopeKey(normalized);

            if (accepted.Any(a => string.Equals(a.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)))
            {
                rejections.Add(new SupplyScopeRejection(
                    normalized,
                    SupplyRejectionReason.Duplicate,
                    $"同一 scope 在请求中重复出现（规范化后相同）：{normalized}"));
                continue;
            }

            var nestedAgainst = accepted.FirstOrDefault(a =>
                IsAncestorOrSame(a.ScopeKey, scopeKey) || IsAncestorOrSame(scopeKey, a.ScopeKey));
            if (nestedAgainst is not null)
            {
                rejections.Add(new SupplyScopeRejection(
                    normalized,
                    SupplyRejectionReason.Nested,
                    $"scope 与已接受的 scope 存在父子嵌套（任一方向都不允许）：{normalized} ↔ {nestedAgainst.RootPath}"));
                continue;
            }

            accepted.Add(new SupplyScope(scopeKey, normalized));
        }

        return new NormalizationResult(accepted, rejections);
    }

    /// <summary>规范键：分隔符统一为 <c>\</c> 后做不变文化小写（Windows 文件系统大小写不敏感）。</summary>
    internal static string ToScopeKey(string normalizedPath) =>
        normalizedPath.Replace('/', '\\').ToLowerInvariant();

    /// <summary>
    /// 规范化键下的「祖先**或相同**」判定（<b>本组件唯一实现</b>，两侧入口都必须经它）。
    /// <para>
    /// 入参必须是 <see cref="ToScopeKey"/> 的产物（已小写、分隔符已统一为 <c>\</c>）。
    /// </para>
    /// <para>
    /// ⚠️ 祖先键**本身**若以 <c>\</c> 结尾（盘根 <c>c:\</c>），**不再补一个分隔符**：
    /// 旧实现无条件拼 <c>"\\"</c>，于是盘根得到 <c>c:\\</c> 前缀 ⇒ 盘根 scope 的
    /// 父子嵌套 / 越界判定**恒为 false**（缺陷 ① 的同族第二处）。
    /// </para>
    /// </summary>
    /// <param name="candidateAncestor">候选祖先键。</param>
    /// <param name="candidateDescendant">候选后代键。</param>
    internal static bool IsAncestorOrSame(string candidateAncestor, string candidateDescendant)
    {
        if (string.Equals(candidateAncestor, candidateDescendant, StringComparison.Ordinal))
            return true;

        var prefix = candidateAncestor.EndsWith('\\')
            ? candidateAncestor
            : candidateAncestor + "\\";

        return candidateDescendant.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 规范化键是否落在 scope 之内（**不含** scope 根自身；根是目录，不可能是文件变更对象）。
    /// <para>本组件唯一实现：维护侧 <c>FullTextChangeCoalescer.IsWithinScope</c> 必须经它。</para>
    /// </summary>
    /// <param name="scopeKey">scope 规范键。</param>
    /// <param name="pathKey">待判定的路径规范键。</param>
    internal static bool IsWithinScopeKey(string scopeKey, string pathKey) =>
        !string.Equals(scopeKey, pathKey, StringComparison.Ordinal)
        && IsAncestorOrSame(scopeKey, pathKey);

    /// <summary>
    /// 去尾分隔符，但**保住盘根**：<c>C:\</c> 不能被裁成 <c>C:</c>（后者是盘符相对路径，语义完全不同）。
    /// <para>
    /// ⚠️ 这是本组件内「保盘根裁剪」的**单一真源**：供给侧规范化与维护侧
    /// <c>FullTextChangeCoalescer.NormalizeComparisonKey</c> 都必须经它 —— 两侧各自复刻时，
    /// 盘根会分别得到 <c>c:\</c> 与 <c>c:</c> 两个键 ⇒ 推出不同租约文件 ⇒ 同一语料根上
    /// 维护直写与供给整目录替换**不互斥**（缺陷 ①）。
    /// </para>
    /// </summary>
    /// <param name="path">已绝对化的路径。</param>
    internal static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
            return path;

        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && trimmed.Length < root.Length)
            return root;

        return trimmed;
    }
}
