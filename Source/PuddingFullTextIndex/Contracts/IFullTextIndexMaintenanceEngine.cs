namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 局部维护的**执行接缝**（方案 §3.2 的 <c>IFullTextIndexMaintenanceEngine</c>）。
/// <para>
/// ⚠️ 与 <see cref="IFullTextSearchEngine"/> / <see cref="IFullTextIndexRootedEngine"/> **完全独立**：
/// 后两者被 CLI 工程的实现 / 替身共同实现，加成员会破坏其编译（CLI 属红线，不可改）。
/// 因此这里以**新增独立接口**的方式扩展，原接口的成员清单逐字不变。
/// </para>
/// <para>
/// ⚠️ 本切片（S1）**只定义契约**，不提供实现，也不在组件里注册任何 DI（实现属 S3）。接口暂时没有实现类是正常的。
/// </para>
/// <para>
/// 实现必须遵守的失败语义（方案 §1.3 / §3.6 / §5.1 / §5.4）：
/// 内容提取**先于**索引删除；单文件失败保留该文件旧文档并进入重试集合；一个变更集通过**单次 commit** 原子可见，
/// 致命失败 rollback；任一路径失败 ⇒ **不推进** checkpoint；预算硬限（集合口径）超限 ⇒ rollback 并保留上一个 commit；
/// 取消必须外抛或返回明确的 <see cref="FullTextMutationState.Cancelled"/>，不得被逐文件隔离逻辑吞掉。
/// </para>
/// </summary>
public interface IFullTextIndexMaintenanceEngine
{
    /// <summary>
    /// 把一个统一变更集应用到 live 索引：对成功 Upsert 先删旧文档再添加当前文档，对确认删除删该路径文档，
    /// 最后单次 commit。成功后由调用方推进 checkpoint（本方法只**报告**是否可推进，不自己写 checkpoint）。
    /// </summary>
    /// <param name="changeSet">统一变更集（三源公用）。</param>
    /// <param name="budget">本批生效的预算硬限（集合口径）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<FullTextMutationResult> ApplyChangesAsync(
        FullTextChangeSet changeSet,
        FullTextMutationBudget budget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 枚举索引中**已有**的路径清册。补偿扫描用它做「索引路径集合 − 当前磁盘路径集合 = Delete 候选」，
    /// 这是离线删除与 rename 单侧事件唯一可靠的恢复来源。
    /// <para>只读：不得创建索引目录、不得写索引、不得改 reader 缓存。</para>
    /// </summary>
    /// <param name="scopeRoot">scope 语料根。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    IAsyncEnumerable<IndexedPathEntry> EnumerateIndexedPathsAsync(
        string scopeRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 索引完整性探针：核对 live 索引与磁盘路径集合的差异，并给出三态结论。
    /// <para>
    /// ⚠️ 只读且**绝不自动重建**：索引目录不存在 / 不可读 / 无法证明新鲜 ⇒ 返回
    /// <see cref="FullTextIndexIntegrityState.ManualRebuildRequired"/>（不是「跳过」，更不是偷偷初始化一份全库索引）。
    /// 资源压力（CPU / 磁盘繁忙）不可采样时按「系统繁忙」退避，不得强行运行。
    /// </para>
    /// </summary>
    /// <param name="scopeRoot">scope 语料根。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<FullTextIndexIntegrityProbe> ProbeIntegrityAsync(
        string scopeRoot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 单批局部写入的**预算硬限**（方案 §4.2 / §5.1）：与 A2a 的集合口径一致 ——
/// <b>全部 live scope 已用字节 + 本批实际增长 ≤ <see cref="MaxIndexBytes"/></b>。
/// <para>
/// ⚠️ 「只更新一个文件」**不**豁免预算：局部更新仍会不断产生新段与删除标记，
/// 因此本批仍按同一判定函数计算，且 quota 必须覆盖 commit / merge 产生的所有输出文件。
/// </para>
/// </summary>
/// <param name="MaxIndexBytes">配置的集合总预算（字节，硬限）。必须为正；解析层负责 fail-closed 校验。</param>
/// <param name="LiveIndexBytes">本批开始前实测的**全部** live scope 索引目录字节合计（判定口径的另一半）。</param>
/// <param name="MaxPaths">本批允许处理的路径数上限（防单批过大导致长持 writer lock）。</param>
public sealed record FullTextMutationBudget(
    long MaxIndexBytes,
    long LiveIndexBytes,
    int MaxPaths)
{
    /// <summary>本批还允许增长多少字节（集合口径）；小于等于 0 表示本批不得写入任何新段。</summary>
    public long RemainingBytes => MaxIndexBytes - LiveIndexBytes > 0 ? MaxIndexBytes - LiveIndexBytes : 0;
}

/// <summary>
/// 一次局部写入的**终态**（方案 §5.4 要求显式报告 <c>Applied / Failed / RetainedOld / DeleteCount</c>）。
/// <para>任一状态都不得伪装成完全成功：有失败项 ⇒ <see cref="FullTextMutationState.PartiallyApplied"/>，且不得推进 checkpoint。</para>
/// </summary>
public enum FullTextMutationState
{
    /// <summary>本批全部成功（含「空变更集」这种合法的零变更）。</summary>
    Applied = 0,

    /// <summary>部分成功：成功项已提交，失败 / 保留项存在，checkpoint 不推进。</summary>
    PartiallyApplied = 1,

    /// <summary>被门禁拒绝（预算超限 / 守卫不通过），**未写入**任何东西。</summary>
    Rejected = 2,

    /// <summary>互斥失败（跨进程租约 Busy 或 Lucene writer lock 冲突），<b>未写入</b>；按 Busy/Retry 处理，不推进 checkpoint。</summary>
    Busy = 3,

    /// <summary>取消：必须由调用方观察到，不得被文件隔离逻辑吞掉。</summary>
    Cancelled = 4,

    /// <summary>致命失败：已 rollback（或沿用上一个 commit），不推进 checkpoint。</summary>
    Failed = 5,
}

/// <summary>
/// 一次局部写入的结果（机器可读 + 可诊断）。所有计数字段都指**本批**。
/// </summary>
/// <param name="BatchId">对应变更集的批次标识。</param>
/// <param name="ScopeKey">scope 规范键。</param>
/// <param name="State">终态。</param>
/// <param name="UpsertAppliedCount">已按 delete-then-add 提交的路径数。</param>
/// <param name="DeleteAppliedCount">已从索引删除的路径数。</param>
/// <param name="RetainedOldCount">提取 / 读取失败因而**保留旧文档**的路径数（等价于待重试数）。</param>
/// <param name="FailedCount">本批失败项数（应为 <paramref name="RetainedOldCount"/> 与其它失败项的合计）。</param>
/// <param name="FailedPaths">失败路径（可诊断；不得静默丢弃）。</param>
/// <param name="RetainedOldPaths">保留了旧文档的路径（下次必须重放）。</param>
/// <param name="IndexBytesBefore">提交前实测字节；不可测时为 null（<b>不得</b>伪报 0）。</param>
/// <param name="IndexBytesAfter">提交后实测字节；不可测时为 null。</param>
/// <param name="CommitMilliseconds">commit 耗时（毫秒）；未提交时为 null。</param>
/// <param name="CheckpointAdvanced">本批是否允许推进 checkpoint（全部成功且为完整补偿轮次时为 true）。</param>
/// <param name="Message">可读说明（Busy 原因 / 预算拒绝原因 / 提交失败原因）。</param>
public sealed record FullTextMutationResult(
    string BatchId,
    string ScopeKey,
    FullTextMutationState State,
    int UpsertAppliedCount,
    int DeleteAppliedCount,
    int RetainedOldCount,
    int FailedCount,
    IReadOnlyList<string> FailedPaths,
    IReadOnlyList<string> RetainedOldPaths,
    long? IndexBytesBefore,
    long? IndexBytesAfter,
    double? CommitMilliseconds,
    bool CheckpointAdvanced,
    string? Message);

/// <summary>
/// 索引中一条**已有路径**的清册条目（供补偿扫描算 Delete 候选）。
/// </summary>
/// <param name="FullPath">索引文档里记录的完整路径（保留写入时的大小写）。</param>
/// <param name="NormalizedPath">
/// 折叠 / 比较用的规范化路径：分隔符统一 <c>\</c> + 不变文化小写（与 scope 规范键同口径，Windows 大小写不敏感）。
/// </param>
/// <param name="DocumentCount">该路径的文档数（同一文件会被切成多个 chunk 文档）。</param>
public sealed record IndexedPathEntry(
    string FullPath,
    string NormalizedPath,
    int DocumentCount);

/// <summary>体检结论三态（方案 §3.5 / §3.7）：只标记，不自动重建。</summary>
public enum FullTextIndexIntegrityState
{
    /// <summary>索引存在、可读，且与磁盘路径集合无差异。</summary>
    Healthy = 0,

    /// <summary>索引存在但发现差异（差异应以统一变更集的形式发布出去）。</summary>
    Degraded = 1,

    /// <summary>
    /// 索引目录不存在 / 不可读 / 无法证明新鲜 ⇒ 需要**手动重建**。
    /// 这是终态而不是「跳过」：绝不通过局部写入偷偷创建一份「初始全库索引」。
    /// </summary>
    ManualRebuildRequired = 2,
}

/// <summary>
/// 体检结果：差异清单 + 三态结论（只读探针的返回值；探针本身不写索引、不自动重建）。
/// </summary>
/// <param name="ScopeKey">scope 规范键。</param>
/// <param name="State">三态结论。</param>
/// <param name="IndexDirectoryExists">live 索引目录是否存在（不存在 ⇒ 必然是 <see cref="FullTextIndexIntegrityState.ManualRebuildRequired"/>）。</param>
/// <param name="IndexedPathCount">索引里的路径数；不可读时为 null（<b>不得</b>伪报 0）。</param>
/// <param name="IndexedDocumentCount">索引里的文档总数；不可读时为 null。</param>
/// <param name="CheckedPathCount">本轮实际核对的路径数（体检按小切片运行）。</param>
/// <param name="MismatchCount">本轮发现的差异项数。</param>
/// <param name="MismatchedPaths">差异路径（可诊断）。</param>
/// <param name="IndexBytes">live 索引目录字节；不可测时为 null。</param>
/// <param name="Message">可读说明（损坏 / 不可读 / 退避原因）。</param>
public sealed record FullTextIndexIntegrityProbe(
    string ScopeKey,
    FullTextIndexIntegrityState State,
    bool IndexDirectoryExists,
    int? IndexedPathCount,
    int? IndexedDocumentCount,
    int CheckedPathCount,
    int MismatchCount,
    IReadOnlyList<string> MismatchedPaths,
    long? IndexBytes,
    string? Message);
