using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// 语料字节 → 索引字节的预测口径（干跑估算的唯一系数来源）。
/// </summary>
internal static class SupplyIndexSizeEstimator
{
    /// <summary>
    /// 实测依据（本组件唯一允许出现该系数的地方）：
    /// 整仓语料约 <b>744 MB</b>（Lucene 全量构建口径）时，生成索引目录约 <b>838 MB</b>
    /// ⇒ 838 / 744 ≈ 1.126，保守取整为 <b>1.13</b>。
    /// <para>
    /// 依据来源：ADR-089 全文索引容量实测（报告 §5「1 GiB 预算 vs 实测体积」）。
    /// <b>禁止</b>在别处再写一个魔数；要改口径请改这里并同步更新本注释的实测依据。
    /// </para>
    /// </summary>
    internal const double IndexToCorpusRatio = 1.13;

    /// <summary>预测索引字节数（向上取整；语料为 0 时返回 0）。</summary>
    internal static long PredictIndexBytes(long corpusBytes) =>
        corpusBytes <= 0 ? 0 : (long)Math.Ceiling(corpusBytes * IndexToCorpusRatio);
}

/// <summary>job 标识生成（唯一、可读前缀便于日志检索）。</summary>
internal static class SupplyJobNaming
{
    internal const string JobIdPrefix = "supply-";

    internal static string NewJobId() => JobIdPrefix + Guid.NewGuid().ToString("N");
}
