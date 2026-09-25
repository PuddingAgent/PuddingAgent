using System.Security.Cryptography;
using System.Text;

namespace PuddingFullTextIndex.Infrastructure;

/// <summary>
/// 「索引 patterns → 指纹」计算的**唯一实现**（S1b）。
/// <para>
/// <b>这是 <c>.last_indexed</c> 的 <c>p</c> 字段（<c>patternHash</c>）的唯一真源</b>：任何第二处复刻
/// （哪怕是"顺手内联一份"）都算真源分裂 —— 两处一旦漂移，同一份 patterns 会算出两个指纹，
/// 引擎会把"patterns 没变"误判为"变了"，从而每次启动都触发一次**全量重建**。
/// </para>
/// <para>
/// 规则（<b>逐字冻结</b>）：
/// <c>filePatterns ?? "(default)"</c> → <c>SHA256</c>（UTF-8 字节）→ <b>小写 hex</b> → 取前 <b>12</b> 字符。
/// </para>
/// <list type="number">
/// <item><description><b>只有 <c>null</c> 才回退 <c>"(default)"</c></b>；<c>""</c>（空串）<b>不回退</b>
/// （空串的指纹是 <c>e3b0c44298fc</c>，即空输入的 SHA256 前缀 —— 与缺省值<b>不同</b>）。</description></item>
/// <item><description><b>大小写敏感</b>：<c>"(default)"</c> ≠ <c>"(DEFAULT)"</c>
/// （<c>b3ffbbff2d64</c> ≠ <c>3d3f863b69d4</c>），不做任何归一化。</description></item>
/// <item><description><b>不得</b>改成大写 hex（<c>Convert.ToHexString</c>）、<b>不得</b>改截断长度（12）、
/// <b>不得</b>换其它哈希算法 —— 这三条任意一条都会<b>静默</b>让全部现存 <c>.last_indexed</c> 判定为
/// "patterns 变了"，进而触发一次全量重建（本类存在的全部目的就是避免这个）。</description></item>
/// </list>
/// <para>
/// ⚠️ 本类<b>不是</b>"语料根 → 索引目录名"的命名哈希 —— 那是
/// <see cref="FullTextIndexPaths"/> 的<b>唯一</b>职责（输入是绝对化 + 大写后的全路径，输出 64 位 hex）。
/// 两者输入口径与输出长度都不同，<b>不可合并</b>。
/// </para>
/// <para>
/// 消费方：<c>LuceneSearchEngine.BuildIndexInternalAsync</c>（写入 <c>.last_indexed.p</c> 并与读回的
/// <c>p</c> 比较以决定增量/全量）。维护层（S3 的 <c>policyFingerprint</c>）将来在 patterns 这一层复用本方法。
/// </para>
/// </summary>
public static class FullTextPolicyFingerprint
{
    /// <summary>
    /// 计算 patterns 指纹：<paramref name="filePatterns"/> 为 <c>null</c> 时按字面量
    /// <c>"(default)"</c> 处理，然后 SHA256(UTF-8) 取小写 hex 的前 12 字符。
    /// <para>
    /// 纯函数、无 I/O、不抛异常（<paramref name="filePatterns"/> 为 <c>null</c> 是<b>合法</b>输入）。
    /// </para>
    /// </summary>
    /// <param name="filePatterns">
    /// 引擎的 <c>filePatterns</c> 原文（分号分隔的 glob 列表，如 <c>"*.cs;*.md"</c>）。
    /// <c>null</c> ⇒ 视为 <c>"(default)"</c>；<c>""</c> ⇒ 就是空输入（<b>不回退</b>）。
    /// </param>
    public static string ComputePatternFingerprint(string? filePatterns)
    {
        var input = filePatterns ?? "(default)";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash)[..12];
    }
}
