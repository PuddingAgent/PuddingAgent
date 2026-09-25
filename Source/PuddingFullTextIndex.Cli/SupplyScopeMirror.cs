namespace PuddingFullTextIndex.Cli;

/// <summary>
/// 组件内部口径的<b>镜像</b>（跨程序集不可见，故在 CLI 侧逐字复刻）。
/// <para>
/// A19 之后本类只剩**两个**成员（原来的第三个 <c>ResolveIndexDirectory</c> 已删除，见下）：
/// <list type="number">
/// <item><description><c>NormalizeRoot</c> ← 镜像组件 <c>SupplyScopeNormalizer.TrimTrailingSeparators</c>
/// 的「绝对化 → 去尾分隔符但保住盘根」。</description></item>
/// <item><description><c>ToScopeKey</c> ← 镜像 <c>SupplyScopeNormalizer.ToScopeKey</c>
/// （分隔符统一 <c>\</c> + 不变文化小写）。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 这两个成员为什么<b>不</b>改调组件公开 helper（A19 决策，勿「顺手统一」）：
/// <list type="number">
/// <item><description><c>NormalizeRoot</c> 与 <c>FullTextIndexPaths.NormalizeCorpusRoot</c> <b>语义不同</b>：
/// 前者保住盘根（<c>C:\</c> 不被裁成 <c>C:</c>）且<b>不改大小写</b>（返回值要参与显示与 scopeKey，
/// 见 <c>SupplyCli.ObserveScopeAsync</c>）；后者是索引目录名的**哈希输入**（不保盘根 + 大写）。
/// 硬合并会改变 <c>scopeKey</c> 与 <c>scope[].path</c>。</description></item>
/// <item><description><c>ToScopeKey</c> 镜像的是 <c>SupplyScopeNormalizer.ToScopeKey</c>
/// （服务租约 / 去重 / 幂等键，<b>不是</b>索引目录命名）；该类型是组件 <c>internal</c>，
/// 且组件侧未公开等价 helper —— 此处若自造一个公开 helper，反而会变成**第三处**真源。</description></item>
/// </list>
/// </para>
/// <para>
/// 命名哈希 <c>sha256(大写规范化全路径)</c> 的复刻已在本切片**删除**：CLI 改调组件单一真源
/// <c>FullTextIndexPaths.ResolveIndexDirectory(indexRoot, scopePath)</c>。
/// 由 <c>A19SingleSourceTests</c> 断言「CLI 打印值 == 组件公开 helper == 引擎 ResolveIndexDirectory
/// == 引擎 ProbeDocuments 解析出的目录 == 旧镜像金标准」逐字节相同。
/// </para>
/// <para>
/// ⚠️ 本类仍是 CLI 与组件实现的耦合点：<c>ToScopeKey</c> 口径一旦漂移，<c>status</c> 会报告错误的
/// scopeKey。因此由**真实构建的交叉断言**守护（见 <c>SupplyCliBuildTests</c>）：
/// ① <c>build --json</c> 返回的 <c>scopes[0].scopeKey</c> 来自协调器内部规范化，必须与本镜像一致；
/// ② 真 Lucene 构建后，金标准解析出的索引目录必须**就是**磁盘上引擎实际建出的那个目录。
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
}
