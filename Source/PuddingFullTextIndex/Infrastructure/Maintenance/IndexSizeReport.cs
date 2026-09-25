using System.Globalization;
using System.Text.Json;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 一次局部写入的**体积增长报告**（方案 §6 S3 完成标准⑥：`连续局部更新的体积增长有机器可读报告`）。
/// <para>
/// 纯数据类型 + 两种稳定的机器可读序列化（NDJSON 单行 / TSV 行）：两者都可解析、可断言、可长期归档，
/// <b>不是</b>给人看的日志字符串。字段语义：
/// <list type="bullet">
/// <item><description><see cref="IndexBytesBefore"/> / <see cref="IndexBytesAfter"/> / <see cref="Delta"/>：
/// 本批开始前 / 结束后**实测**的索引目录总字节与差值；不可测时为 <c>null</c>（<b>不得</b>伪报 0）。</description></item>
/// <item><description><see cref="BudgetBytes"/>：集合预算；<see cref="WithinBudget"/> 只有在「字节数可测
/// 且 ≤ 预算」时才为 <c>true</c>（fail-closed：证明不成立就是 <c>false</c>）；写入期是否被配额中止由
/// <see cref="QuotaExceeded"/> 单独表达。</description></item>
/// <item><description><see cref="BytesWrittenByWriter"/>：本批**写出**字节（上界口径，含 flush 与 merge 输出、含被回滚清除的部分）。</description></item>
/// <item><description><see cref="SegmentFilesAdded"/> / <see cref="SegmentFilesRemoved"/>：本会话观测到的
/// 段数据文件创建 / 删除计数（<b>会话级</b>，含合并回收与回滚清理 —— 不是「净存活」）。</description></item>
/// </list>
/// </para>
/// </summary>
/// <param name="BatchId">变更集批次标识。</param>
/// <param name="ScopeKey">scope 规范键。</param>
/// <param name="Outcome">本批终态（<c>FullTextMutationState</c> 的名字）。</param>
/// <param name="IndexBytesBefore">本批开始前实测索引目录字节；不可测为 null。</param>
/// <param name="IndexBytesAfter">本批结束后实测索引目录字节；不可测为 null。</param>
/// <param name="Delta">后 − 前；任一不可测为 null。</param>
/// <param name="BudgetBytes">集合预算（字节）。</param>
/// <param name="WithinBudget">是否可证明「本批**结束后**仍在预算内」（可测且 ≤ 预算；不可测 ⇒ false，fail-closed）。
/// ⚠️ 它与 <see cref="QuotaExceeded"/> 是**两个维度**，必须一起读：
/// <c>Outcome=Rejected + QuotaExceeded=true + WithinBudget=true</c> = 「写入期越界已被中止、回滚后磁盘状态仍在
/// 预算内」——这正是 §4.1 硬门禁要看的形态；把两者折成一个标志会掩盖「中止到底发生过没有」。</param>
/// <param name="CommitMilliseconds">commit 耗时（毫秒）；未提交为 null。</param>
/// <param name="SegmentFilesAdded">本会话创建的段数据文件数（以 <c>_</c> 开头）。</param>
/// <param name="SegmentFilesRemoved">本会话删除的段数据文件数（含合并回收 / 回滚清理）。</param>
/// <param name="WriterSessionOpened">本批是否真的打开过 writer 会话（false ⇒ 「没有超限」不是证据，而是「压根没写」）。</param>
/// <param name="AllowedGrowthBytes">本批允许的输出增长上限（集合预算 − 开始前 live 字节）。</param>
/// <param name="BytesWrittenByWriter">本批累计写出字节（上界口径）。</param>
/// <param name="QuotaExceeded">写入期配额是否被触发（越界事实）。</param>
/// <param name="OvershootBytes">越界量；未越界为 0。</param>
/// <param name="FilesCreated">本会话创建的文件总数（含 <c>segments_N</c>）。</param>
/// <param name="FilesDeleted">本会话删除的文件总数。</param>
/// <param name="RolledBack">本批是否走了 rollback（未提交）。</param>
/// <param name="FilesRemovedByRollback">其中由 rollback 清理掉的文件数（残余为 0 的证据之一）。</param>
internal sealed record IndexSizeReport(
    string BatchId,
    string ScopeKey,
    string Outcome,
    long? IndexBytesBefore,
    long? IndexBytesAfter,
    long? Delta,
    long BudgetBytes,
    bool WithinBudget,
    double? CommitMilliseconds,
    int SegmentFilesAdded,
    int SegmentFilesRemoved,
    bool WriterSessionOpened,
    long AllowedGrowthBytes,
    long BytesWrittenByWriter,
    bool QuotaExceeded,
    long OvershootBytes,
    int FilesCreated,
    int FilesDeleted,
    bool RolledBack,
    int FilesRemovedByRollback)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>TSV 表头（列顺序与 <see cref="ToTsvRow"/> / <see cref="FromTsvRow"/> 逐字一致）。</summary>
    internal static string TsvHeader =>
        "BatchId\tScopeKey\tOutcome\tIndexBytesBefore\tIndexBytesAfter\tDelta\tBudgetBytes\tWithinBudget\t"
        + "CommitMilliseconds\tSegmentFilesAdded\tSegmentFilesRemoved\tWriterSessionOpened\tAllowedGrowthBytes\t"
        + "BytesWrittenByWriter\tQuotaExceeded\tOvershootBytes\tFilesCreated\tFilesDeleted\tRolledBack\tFilesRemovedByRollback";

    /// <summary>NDJSON 单行（无缩进、字段顺序稳定）。</summary>
    internal string ToJsonLine() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>把 <see cref="ToJsonLine"/> 的产物解析回来（null 表示该行不可解析 —— 调用方必须显式处理）。</summary>
    internal static IndexSizeReport? FromJsonLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<IndexSizeReport>(line, JsonOptions);

    /// <summary>TSV 数据行（null 一律写成空字段；double 用不变文化的往返格式）。</summary>
    internal string ToTsvRow() => string.Join(
        '\t',
        BatchId,
        ScopeKey,
        Outcome,
        Nullable(IndexBytesBefore),
        Nullable(IndexBytesAfter),
        Nullable(Delta),
        BudgetBytes.ToString(CultureInfo.InvariantCulture),
        WithinBudget ? "true" : "false",
        CommitMilliseconds is { } ms ? ms.ToString("R", CultureInfo.InvariantCulture) : string.Empty,
        SegmentFilesAdded.ToString(CultureInfo.InvariantCulture),
        SegmentFilesRemoved.ToString(CultureInfo.InvariantCulture),
        WriterSessionOpened ? "true" : "false",
        AllowedGrowthBytes.ToString(CultureInfo.InvariantCulture),
        BytesWrittenByWriter.ToString(CultureInfo.InvariantCulture),
        QuotaExceeded ? "true" : "false",
        OvershootBytes.ToString(CultureInfo.InvariantCulture),
        FilesCreated.ToString(CultureInfo.InvariantCulture),
        FilesDeleted.ToString(CultureInfo.InvariantCulture),
        RolledBack ? "true" : "false",
        FilesRemovedByRollback.ToString(CultureInfo.InvariantCulture));

    /// <summary>解析 <see cref="ToTsvRow"/> 的产物（列数不符 / 数值不可解析 ⇒ 抛异常，绝不静默返回默认值）。</summary>
    internal static IndexSizeReport FromTsvRow(string row)
    {
        var cells = row.Split('\t');
        if (cells.Length != 20)
            throw new FormatException($"体积增长报告 TSV 列数必须为 20，实际 {cells.Length}。");

        return new IndexSizeReport(
            cells[0],
            cells[1],
            cells[2],
            NullableInt64(cells[3]),
            NullableInt64(cells[4]),
            NullableInt64(cells[5]),
            long.Parse(cells[6], CultureInfo.InvariantCulture),
            ParseBool(cells[7]),
            string.IsNullOrEmpty(cells[8]) ? null : double.Parse(cells[8], NumberStyles.Float, CultureInfo.InvariantCulture),
            int.Parse(cells[9], CultureInfo.InvariantCulture),
            int.Parse(cells[10], CultureInfo.InvariantCulture),
            ParseBool(cells[11]),
            long.Parse(cells[12], CultureInfo.InvariantCulture),
            long.Parse(cells[13], CultureInfo.InvariantCulture),
            ParseBool(cells[14]),
            long.Parse(cells[15], CultureInfo.InvariantCulture),
            int.Parse(cells[16], CultureInfo.InvariantCulture),
            int.Parse(cells[17], CultureInfo.InvariantCulture),
            ParseBool(cells[18]),
            int.Parse(cells[19], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 由「本批结果 + 预算 + writer 会话观测」组装报告（唯一构造入口：避免字段口径分叉）。
    /// <para>
    /// <see cref="WithinBudget"/> 是 **fail-closed 的证明**：必须同时满足
    /// ① 结束后字节可测、② ≤ 预算、③ 没有触发写入期配额；任一不成立即为 <c>false</c>。
    /// </para>
    /// </summary>
    internal static IndexSizeReport Create(
        Contracts.FullTextMutationResult result,
        Contracts.FullTextMutationBudget budget,
        IndexSizeObservation observation)
    {
        var before = result.IndexBytesBefore;
        var after = result.IndexBytesAfter;

        return new IndexSizeReport(
            result.BatchId,
            result.ScopeKey,
            result.State.ToString(),
            before,
            after,
            before is { } b && after is { } a ? a - b : null,
            budget.MaxIndexBytes,
            WithinBudget: after is { } measured && measured <= budget.MaxIndexBytes,
            result.CommitMilliseconds,
            observation.SegmentFilesAdded,
            observation.SegmentFilesRemoved,
            observation.WriterSessionOpened,
            observation.AllowedGrowthBytes,
            observation.BytesWrittenByWriter,
            observation.QuotaExceeded,
            observation.OvershootBytes,
            observation.FilesCreated,
            observation.FilesDeleted,
            observation.RolledBack,
            observation.FilesRemovedByRollback);
    }

    private static string Nullable(long? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static long? NullableInt64(string cell) =>
        string.IsNullOrEmpty(cell) ? null : long.Parse(cell, CultureInfo.InvariantCulture);

    private static bool ParseBool(string cell) => cell switch
    {
        "true" => true,
        "false" => false,
        _ => throw new FormatException($"布尔字段必须是 true/false，实际 '{cell}'。"),
    };
}

/// <summary>
/// 一次批次内 writer 会话的**观测累加器**（可变的内部状态，不进契约）。
/// <para>由 <c>LuceneFullTextIndexMaintenanceEngine</c> 在批次开始时创建、交给 writer 会话填充，
/// 批次结束时交给 <see cref="IndexSizeReport.Create"/> 组装成机器可读报告。</para>
/// <para>默认值表示「本批没有打开 writer 会话」（<see cref="WriterSessionOpened"/> = false）——
/// 这与「打开了但没写」必须可区分（否则会把「没写」当成「没超限」的证据）。</para>
/// </summary>
internal sealed class IndexSizeObservation
{
    /// <summary>本批是否真的打开过 writer 会话（拿到了目录 / writer）。</summary>
    internal bool WriterSessionOpened { get; set; }

    /// <summary>本批允许的输出增长上限。</summary>
    internal long AllowedGrowthBytes { get; set; }

    /// <summary>本批累计写出字节。</summary>
    internal long BytesWrittenByWriter { get; set; }

    /// <summary>是否触发写入期配额。</summary>
    internal bool QuotaExceeded { get; set; }

    /// <summary>越界量。</summary>
    internal long OvershootBytes { get; set; }

    /// <summary>本会话创建 / 删除的文件数（含 <c>segments_N</c>）。</summary>
    internal int FilesCreated { get; set; }

    /// <summary>见 <see cref="FilesCreated"/>。</summary>
    internal int FilesDeleted { get; set; }

    /// <summary>本会话创建的段数据文件数。</summary>
    internal int SegmentFilesAdded { get; set; }

    /// <summary>本会话删除的段数据文件数。</summary>
    internal int SegmentFilesRemoved { get; set; }

    /// <summary>本批是否走了 rollback。</summary>
    internal bool RolledBack { get; set; }

    /// <summary>其中由 rollback 清理掉的文件数。</summary>
    internal int FilesRemovedByRollback { get; set; }

    /// <summary>把包装层观测到的计数收敛进本对象（会话结束时调用一次）。</summary>
    internal void Capture(QuotaEnforcingDirectory directory, bool rolledBack, int filesRemovedByRollback)
    {
        WriterSessionOpened = true;
        AllowedGrowthBytes = directory.AllowedGrowthBytes;
        BytesWrittenByWriter = directory.BytesWritten;
        QuotaExceeded = directory.QuotaExceeded;
        OvershootBytes = directory.Violation?.OvershootBytes ?? 0;
        FilesCreated = directory.CreatedFileCount;
        FilesDeleted = directory.DeletedFileCount;
        SegmentFilesAdded = directory.SegmentFileCreatedCount;
        SegmentFilesRemoved = directory.SegmentFileDeletedCount;
        RolledBack = rolledBack;
        FilesRemovedByRollback = filesRemovedByRollback;
    }
}
