namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// **写入期配额超限**（方案 §4.1 硬门禁 / §4.2「在超限前拒绝继续写」）的专用异常。
/// <para>
/// 抛出点唯一：<see cref="QuotaEnforcingDirectory"/> 的记账路径 —— 任何一个
/// <c>IndexOutput</c> 写出后，本批累计输出字节一旦 **越过**「本批允许增长」就立刻抛出，
/// 该次写入<b>不发生</b>（先记账、后写盘）。因此「超限」总是发生在
/// <b>commit 点写盘之前</b>这个可回滚位置，调用方据此 <c>Rollback()</c> 保留上一个 commit。
/// </para>
/// <para>
/// ⚠️ 这个异常可能被 Lucene 包装（例如后台合并路径会被折成
/// <c>MergePolicy.MergeException</c>），因此调用方**判定超限事实必须读
/// <see cref="QuotaEnforcingDirectory.Violation"/>**，不得依赖异常类型或消息文本。
/// </para>
/// </summary>
internal sealed class IndexWriteQuotaExceededException : IOException
{
    internal IndexWriteQuotaExceededException(
        string fileName,
        long bytesWritten,
        long allowedGrowthBytes,
        long budgetBytes)
        : base(
            $"写入期配额超限：写出 '{fileName}' 后本批累计输出 {bytesWritten} 字节 > 本批允许增长 {allowedGrowthBytes} 字节"
            + $"（集合预算 {budgetBytes} 字节）⇒ 中止本批（已写部分将被 rollback 清除）。")
    {
        FileName = fileName;
        BytesWritten = bytesWritten;
        AllowedGrowthBytes = allowedGrowthBytes;
        BudgetBytes = budgetBytes;
    }

    /// <summary>触发越界的那个输出文件名（诊断用）。</summary>
    internal string FileName { get; }

    /// <summary>越界时本批累计输出字节（已写出 + 本次）。</summary>
    internal long BytesWritten { get; }

    /// <summary>本批允许的输出增长字节（集合预算 − 本批开始前 live 索引字节）。</summary>
    internal long AllowedGrowthBytes { get; }

    /// <summary>集合预算（<c>FullTextMutationBudget.MaxIndexBytes</c>）。</summary>
    internal long BudgetBytes { get; }

    /// <summary>越界量（正数；0 表示刚好等于允许值，不可能出现 —— 抛出条件是严格大于）。</summary>
    internal long OvershootBytes => BytesWritten - AllowedGrowthBytes;
}
