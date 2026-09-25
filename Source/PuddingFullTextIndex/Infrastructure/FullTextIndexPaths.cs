using System.Security.Cryptography;
using System.Text;

namespace PuddingFullTextIndex.Infrastructure;

/// <summary>
/// 「语料根 → 索引目录名」命名哈希的**单一真源**（A19）。
/// <para>
/// 规则（<b>逐字冻结</b>，改动会让既有索引目录全部失配 = 索引凭空消失）：
/// <c>Path.GetFullPath(path)</c> → <c>TrimEnd(DirectorySeparatorChar, AltDirectorySeparatorChar)</c>
/// → <c>ToUpperInvariant()</c> → <c>SHA256</c>（UTF-8 字节）→ <b>小写 hex</b>（64 位）
/// → <c>Path.Combine(indexRoot, hash)</c>。
/// </para>
/// <para>
/// 消费方：<c>LuceneSearchEngine.GetIndexDirectoryPath</c>（构建 / 搜索 / 失效 / 缓存键共 10 处调用点）、
/// <c>IFullTextIndexRootedEngine.ResolveIndexDirectory</c> 与 <c>ProbeDocuments</c>（经前者）、
/// CLI <c>status</c> 的索引目录显示（A19 起经本类的 <see cref="ResolveIndexDirectory(string, string)"/>）。
/// 任何一处都**不得**再复刻本规则。
/// </para>
/// <para>
/// ⚠️ 与 <c>SupplyScopeNormalizer.TrimTrailingSeparators</c> + <c>SupplyScopeNormalizer.ToScopeKey</c>
/// 是**两套口径**，<b>不可合并</b>（后者服务租约 / 去重 / 幂等键）：
/// <list type="number">
/// <item><description>盘根：本规则<b>不</b>保住盘根（<c>C:\</c> 被裁成 <c>C:</c> —— 引擎既有语义，
/// 改了会改变这类语料根的哈希）；作用域规范化会保成 <c>C:\</c>。</description></item>
/// <item><description>大小写：本规则<b>大写</b>（哈希输入）；作用域键走不变文化<b>小写</b>。</description></item>
/// <item><description>分隔符：本规则不额外归一分隔符（<c>GetFullPath</c> 已按平台归一）；
/// 作用域键显式把 <c>/</c> 换成 <c>\</c>。</description></item>
/// </list>
/// </para>
/// </summary>
public static class FullTextIndexPaths
{
    /// <summary>
    /// 命名哈希的**输入**：绝对化 + 去尾分隔符（<b>不</b>保住盘根）+ 不变文化大写。
    /// <para>
    /// ⚠️ 返回值<b>只</b>用作哈希输入，不是可展示 / 可直接使用的路径（大写、且盘根会被去掉尾分隔符）。
    /// </para>
    /// </summary>
    public static string NormalizeCorpusRoot(string corpusRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRootPath);

        return Path.GetFullPath(corpusRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
    }

    /// <summary>
    /// 把语料根映射为**指定索引根**下的索引目录绝对路径（目录可能尚不存在）。
    /// 与引擎构建 / 搜索使用的目录名完全一致（同一实现）。
    /// </summary>
    /// <param name="indexRootDirectory">索引根目录（引擎的 <c>FullTextIndexOptions.IndexRootDirectory</c>）。</param>
    /// <param name="corpusRootPath">语料根目录（不是索引目录）。</param>
    public static string ResolveIndexDirectory(string indexRootDirectory, string corpusRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRootDirectory);

        var normalized = NormalizeCorpusRoot(corpusRootPath);
        return Path.Combine(indexRootDirectory, Sha256HexLower(normalized));
    }

    /// <summary>SHA256（UTF-8）后的 64 位小写十六进制。</summary>
    private static string Sha256HexLower(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
