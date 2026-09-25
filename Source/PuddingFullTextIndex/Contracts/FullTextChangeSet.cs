namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 统一变更集里一个路径的**最终动作**（方案 §3.1）。
/// <para>
/// 只区分两态：<see cref="Upsert"/> = 「当前应当是索引中的一个可索引文件」，<see cref="Delete"/> = 「当前不应出现在索引里」。
/// 「提取失败保留旧文档」「单文件失败只隔离该文件」**不在这里表达** —— 那是执行层
/// （<see cref="IFullTextIndexMaintenanceEngine"/>）的失败语义；变更集只描述**期望的最终状态**，不描述过程。
/// </para>
/// </summary>
public enum FullTextChangeKind
{
    /// <summary>该路径应当存在于索引中（执行层用 delete-then-add 幂等重写，重复投递无副作用）。</summary>
    Upsert = 0,

    /// <summary>
    /// 该路径不应出现在索引中：不存在 / 变成目录 / 扩展名不再允许 / 命中噪声规则 / 空文件 / 超限文件。
    /// </summary>
    Delete = 1,
}

/// <summary>
/// 变更的**来源位标**（方案 §3.1）：三个触发源 —— watcher（低延迟）、mtime 补偿扫描（覆盖停机期 /
/// overflow / 离线删除 / rename 单侧）、低优先级体检（差异核对）。
/// <para>
/// 多来源命中同一路径时按**位或**合并，仅供诊断与优先级判断；<b>不参与任何正确性判定</b> ——
/// 正确性只看最终动作与最终 stat。
/// </para>
/// </summary>
[Flags]
public enum FullTextChangeSource
{
    /// <summary>文件系统 watcher 的实时事件。</summary>
    Watcher = 1,

    /// <summary>mtime checkpoint 补偿扫描。</summary>
    MTimeRecovery = 2,

    /// <summary>低优先级体检（差异核对 / 损坏探测）。</summary>
    IntegrityCheck = 4,
}

/// <summary>
/// 一个文件的变更事实：**路径 + 最终动作 + 观察到的 stat + 来源位标**（方案 §3.1 原文形状）。
/// <para>
/// ⚠️ <paramref name="LastWriteUtc"/> 与 <paramref name="Length"/> 是**观察值**，不是推断值：
/// 读不到就是 null。**不得**用扫描时刻 / <c>UtcNow</c> / <c>0</c> 顶替 —— 顶替会把「读不到」伪装成
/// 「读到了某个值」，下游据此比较会得出虚假的「未变化」结论（与 <c>.last_indexed</c> 侧
/// <c>?? "{}"</c> 那一类错误同源）。
/// </para>
/// </summary>
/// <param name="FullPath">绝对路径（保留观察方给的大小写，用于读盘与诊断）。</param>
/// <param name="Kind">最终动作。</param>
/// <param name="LastWriteUtc">观察到的最后写入时刻（UTC）；读不到时为 null。</param>
/// <param name="Length">观察到的字节长度；读不到时为 null。</param>
/// <param name="Sources">来源位标（多来源按位或合并）。</param>
public sealed record FullTextFileChange(
    string FullPath,
    FullTextChangeKind Kind,
    DateTimeOffset? LastWriteUtc,
    long? Length,
    FullTextChangeSource Sources);

/// <summary>
/// **统一变更集**（方案 §3.1）：watcher / mtime 补偿 / 体检三源产出同一种数据，执行层只认这一种。
/// <para>
/// 手动重建**不产出**本结构：它的失效半径、安全门禁与发布方式都不同（整库替换 vs 局部提交），
/// 强行统一会模糊两者的边界。
/// </para>
/// <para>
/// ⚠️ 幂等性来源：正确性依赖「按路径 delete-then-add」这一幂等动作，**不**依赖内存队列恰好只投递一次。
/// 因此同一路径被重复处理是允许的（代价小于漏处理），漏掉文件不允许。
/// </para>
/// </summary>
/// <param name="BatchId">批次标识：诊断与幂等审计用；<b>不作为</b>正确性唯一依据。</param>
/// <param name="ScopeRoot">scope 语料根（绝对路径，保留调用方给的大小写）。</param>
/// <param name="ScopeKey">
/// scope 规范键（与既有供给链同口径：分隔符统一 <c>\</c> + 不变文化小写，见 <c>SupplyScopeNormalizer.ToScopeKey</c>）。
/// 租约 / gate / job 合并都以它为准。
/// </param>
/// <param name="Changes">按路径去重后的最终动作：同一规范化路径在本列表里**最多出现一次**。</param>
/// <param name="ScanStartedUtc">
/// 本轮扫描的**开始**时刻（UTC）。checkpoint 只能取这个值，**不能**取结束时刻 ——
/// 取结束时刻会让「已经枚举过它、随后才被写入」的文件（其新 mtime 小于结束时刻）在下一轮被永久跳过。
/// </param>
/// <param name="PreviousWatermarkUtc">
/// 本轮开始时读到的旧 watermark；无 checkpoint / 不可读 / 版本不支持时为 null（⇒ 按陈旧处理、需全范围校准，
/// <b>绝不</b>按「无需维护」处理）。
/// </param>
/// <param name="RequiresCheckpointAdvance">
/// 本轮成功后是否允许推进 checkpoint。任一路径失败 ⇒ 必须为 <c>false</c>：
/// 成功的文件可以提交，但全局 watermark 不前进（下次重放，允许，不漏掉）。
/// </param>
public sealed record FullTextChangeSet(
    string BatchId,
    string ScopeRoot,
    string ScopeKey,
    IReadOnlyList<FullTextFileChange> Changes,
    DateTimeOffset? ScanStartedUtc,
    DateTimeOffset? PreviousWatermarkUtc,
    bool RequiresCheckpointAdvance);
