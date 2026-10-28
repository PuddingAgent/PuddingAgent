namespace PuddingRuntime.Tools.BuiltIns.Search;

/// <summary>
/// 解析后的索引作用域：<paramref name="IndexRoot"/> 是**实际被索引的祖先目录**，
/// <paramref name="SubDirectoryFilter"/> 是请求目录相对该祖先的**相对路径**
/// （与 <c>LuceneSearchEngine</c> 的过滤语义一致：它用 <c>Path.GetRelativePath(directoryPath, path)</c>
/// 再按前缀比较，所以这里必须给相对路径，不能给绝对路径）。
/// </summary>
internal readonly record struct IndexScope(string IndexRoot, string? SubDirectoryFilter);

/// <summary>
/// 把「请求检索的目录」解析成「实际有索引的目录 + 子目录过滤前缀」。
///
/// 背景（2026-10-28 实测）：索引按**注册项目根**建立，而 <c>LuceneSearchEngine.SearchAsync</c>
/// 要求 <c>HasIndex(directoryPath)</c>。于是任何向下收窄的目录检索都直接报
/// 「scope has no index yet」，被迫退回托管扫描并撞 2000 文件 / 64MB / 10s 上限 ——
/// 明明祖先目录有索引、引擎也支持子目录过滤，却用不上。
///
/// 本类型只做**目录解析**：不读索引内容、不碰文件系统（除了规范化路径），
/// 索引存在性由调用方以谓词注入，因此可被纯函数单测覆盖。
///
/// ⚠️ <see cref="IndexScope.IndexRoot"/> 保留的是**请求侧的写法**：<see cref="Path.GetFullPath(string)"/>
/// 不会把路径归一成盘上的实际大小写。这不会导致找不到索引——引擎用
/// <c>FullTextIndexPaths.NormalizeCorpusRoot</c>（<c>ToUpperInvariant</c> 后再 SHA256）定位索引目录，
/// 大小写不敏感。不要为了「看起来整齐」而在这里做大小写改写。
/// </summary>
internal static class IndexScopeResolver
{
    /// <summary>
    /// 从 <paramref name="requestedDirectory"/> 开始向上找最近的有索引目录。
    /// </summary>
    /// <param name="requestedDirectory">请求检索的目录（绝对或相对，内部会 <see cref="Path.GetFullPath(string)"/>）。</param>
    /// <param name="hasIndex">索引存在性谓词；由调用方绑定到具体引擎。</param>
    /// <param name="workspaceRoot">
    /// 上溯边界（含），**仅在它确实是 <paramref name="requestedDirectory"/> 的祖先时生效**。
    /// 请求目录不在边界内（例如索引 scope 配在工作区之外）时，边界对该请求无意义，
    /// 上溯照常进行——否则上溯会在第一次迭代就返回 null，等于没修。
    /// 为空则不设边界。
    /// </param>
    /// <returns>解析成功返回作用域；请求目录及其所有边界内祖先都没有索引时返回 null。</returns>
    internal static IndexScope? Resolve(
        string? requestedDirectory,
        Func<string, bool>? hasIndex,
        string? workspaceRoot = null)
    {
        if (string.IsNullOrWhiteSpace(requestedDirectory) || hasIndex is null)
            return null;

        if (!TryNormalize(requestedDirectory, out var requested))
            return null;

        string? boundary = null;
        if (!string.IsNullOrWhiteSpace(workspaceRoot) && TryNormalize(workspaceRoot!, out var normalizedRoot))
            boundary = normalizedRoot;

        var candidate = requested;
        while (candidate is not null)
        {
            // 先看“这个目录自己有没有索引”，**再**看边界。
            // 顺序很重要：边界只是限制“向上借用祖先索引”，不应该否定“请求目录自己就有索引”
            // 这个合法情形——否则工作区外但确实已建索引的目录会被误拒
            //（2026-10-28：`Backend_Index_Reports_Engine_Failure_Distinctly_From_Not_Indexed` 当场抓到了这个回归）。
            if (hasIndex(candidate))
            {
                if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                    return new IndexScope(candidate, null);

                var relative = Path.GetRelativePath(candidate, requested);
                // GetRelativePath 对同一路径返回 "."；此时等价于「就是该作用域本身」。
                if (string.IsNullOrWhiteSpace(relative) || relative == ".")
                    return new IndexScope(candidate, null);

                return new IndexScope(candidate, relative);
            }

            // 未命中索引才考虑上溯；上溯不得越过边界。
            //
            // ★ 边界只在「它确实是请求目录的祖先」时才成立。
            //   反例（2026-10-28 宿主实测）：本部署的索引 scope 是 `D:\CodeProject\PuddingAgent\PuddingAgent`
            //   （配置在 FullTextIndex:Scopes 里），而工作区根是 `D:\Data\workspaces\default` ——
            //   两者是**互不包含的两棵树**。此时若无条件执行边界检查，第一次上溯就 return null，
            //   上溯等于完全没修：实测仓库根可查（engineMs=12）而其子目录 `Source/PuddingPlatform`
            //   仍报 "has no index yet, and neither does any ancestor"。
            //   安全性不靠边界保证：返回的 SubDirectoryFilter 是相对路径，引擎按前缀过滤，
            //   结果无论借用多远的祖先都必然落在请求目录之内，不会产生跨树的假阳性召回。
            if (boundary is not null
                && IsSameOrDescendant(requested, boundary)
                && !IsSameOrDescendant(candidate, boundary))
                return null;

            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
                break;

            candidate = parent;
        }

        return null;
    }

    private static bool TryNormalize(string path, out string normalized)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // TrimEndingDirectorySeparator 会正确保留根（"C:\" 保持 "C:\"），
            // 而手工 TrimEnd 会把根截成 "C:" —— 那是另一个语义。
            normalized = Path.TrimEndingDirectorySeparator(full);
            return normalized.Length > 0;
        }
        catch
        {
            // 非法路径不抛给调用方：解析失败等同于「没有可用作用域」。
            normalized = string.Empty;
            return false;
        }
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;

        // 必须以「根 + 分隔符」为前缀，否则 C:\ab 会被判成 C:\a 的后代。
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
