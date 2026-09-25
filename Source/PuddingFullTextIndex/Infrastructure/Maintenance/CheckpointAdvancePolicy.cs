using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 「本轮是否允许推进 checkpoint」的**阻止原因**（ASCII、无歧义、一经发布不复用）。
/// <para>
/// 调用方据此区分「因为失败了」「因为取消了」「因为被预算拒绝」「因为互斥未取得」等情形，
/// 从而决定重试 / 退避 / 告警策略；把不同原因折叠成同一个值会让诊断与修复失去依据。
/// </para>
/// </summary>
public enum CheckpointAdvanceBlockReason
{
    /// <summary>允许推进（没有阻止原因）。</summary>
    None = 0,

    /// <summary>终态为 <c>PartiallyApplied</c>：成功项已提交，但本批存在失败 / 保留项。</summary>
    PartiallyApplied = 1,

    /// <summary>终态为 <c>Rejected</c>：被门禁拒绝（预算硬限 / 守卫不通过），**未写入**任何东西。</summary>
    Rejected = 2,

    /// <summary>终态为 <c>Busy</c>：互斥未取得（跨进程租约 / writer lock 冲突），**未写入**任何东西。</summary>
    Busy = 3,

    /// <summary>终态为 <c>Cancelled</c>：本批被取消（方案 §3.6 末句：取消必须 rollback / 外抛，不写 checkpoint）。</summary>
    Cancelled = 4,

    /// <summary>终态为 <c>Failed</c>：致命失败（已 rollback，或沿用上一个 commit）。</summary>
    Failed = 5,

    /// <summary>终态既不是 <c>Applied</c> 也不属于上面任何具名取值（防御分支；fail-closed）。</summary>
    StateNotApplied = 6,

    /// <summary>终态是 <c>Applied</c> 但 <c>FailedCount &gt; 0</c>（契约自相矛盾时仍按 fail-closed 处理）。</summary>
    FailedItemsPresent = 7,

    /// <summary>终态是 <c>Applied</c> 且无失败项，但 <c>RetainedOldCount &gt; 0</c>（存在待重试项）。</summary>
    RetainedOldItemsPresent = 8,
}

/// <summary>
/// 推进判定结果：<see cref="Allowed"/> 为「是否允许推进」，<see cref="Reason"/> 为阻止原因
/// （允许时恒为 <see cref="CheckpointAdvanceBlockReason.None"/>）。
/// </summary>
/// <param name="Allowed">true ⇒ 允许推进 checkpoint。</param>
/// <param name="Reason">阻止原因；允许时为 <see cref="CheckpointAdvanceBlockReason.None"/>。</param>
public readonly record struct CheckpointAdvanceDecision(bool Allowed, CheckpointAdvanceBlockReason Reason)
{
    /// <summary>允许推进的判定（原因恒为 <see cref="CheckpointAdvanceBlockReason.None"/>）。</summary>
    public static CheckpointAdvanceDecision Allow { get; } = new(true, CheckpointAdvanceBlockReason.None);

    /// <summary>不允许推进的判定，并带上原因。</summary>
    /// <param name="reason">阻止原因（不得传 <see cref="CheckpointAdvanceBlockReason.None"/>）。</param>
    public static CheckpointAdvanceDecision Block(CheckpointAdvanceBlockReason reason)
        => new(false, reason);
}

/// <summary>
/// 局部维护的**唯一 checkpoint 推进策略接缝**（纯策略：零 IO、零线程、无副作用、不读时钟）。
/// <para>
/// <b>为什么必须是 fail-closed</b>（方案 §2.4）：checkpoint 的 watermark 取**扫描开始时刻**，
/// 而下一轮的判据是 <c>fileMtime &gt;= watermark - overlap</c>（见 <see cref="MTimeComparison"/>）。
/// 因此只要 watermark 向前推进，任何 mtime 早于它的文件在下一轮就会被**跳过**。
/// 如果某个文件本轮没成功落到索引而 watermark 仍然推进，那个文件的更新就**永久丢失**
/// （下一轮再也不会被处理）。所以规则必须是「**只要本轮有任何一个文件没能成功落到索引，就绝不推进 checkpoint**」，
/// 让下一轮整体重放 —— 方案 §2.4 逐字为：
/// 「某个文件失败、其他文件成功 ⇒ 成功文件可提交，但全局 checkpoint 不推进；失败文件和成功文件下次都会重放」，
/// 以及「**成功文件被重复处理是允许的；漏掉文件不允许**」。
/// </para>
/// <para>
/// <b>判定式</b>（唯一一行，见 <see cref="AllowsAdvance"/>）：
/// 允许推进 ⟺ <c>State == Applied</c> 且 <c>FailedCount == 0</c> 且 <c>RetainedOldCount == 0</c>。
/// <c>PartiallyApplied</c> / <c>Rejected</c> / <c>Busy</c> / <c>Cancelled</c> / <c>Failed</c> 一律不允许推进
/// （方案 §3.6 第 12 步「若这是完整补偿轮次且所有批次成功，原子推进 checkpoint」，末句「取消在第 3～8 步任一点发生时必须 rollback/外抛，不写 checkpoint」）。
/// </para>
/// <para>
/// <b>为什么 <c>RetainedOldCount &gt; 0</c> 也必须阻止推进</b>：retained 的语义是
/// 「提取 / 读取失败 ⇒ 保留旧索引文档并进入待重试集合」（见 <c>FullTextMutationResult.RetainedOldCount</c> 的 XML 注释）。
/// 它与失败**同构**：那些文件的 mtime 早于新 watermark，一旦推进，下一轮被跳过 ⇒ **待重试永远不发生**。
/// 所以两者必须用同一个方向处理，不得因为「旧文档还在、查询还能命中」就认为可以推进
/// —— 那会让索引**永久停留在旧内容**上（索引正确性比重复处理代价重要）。
/// </para>
/// <para>
/// <b>⚠️ 饥饿风险（诚实登记，不藏）</b>：若某个文件**永久**不可读（例如长期被独占锁占住、
/// 权限被撤销、位于永久离线的网络盘），则本策略会让 checkpoint **永不推进** ⇒
/// 每轮都会重扫整个 scope（CPU / IO 成本上升，单批路径数上限也会被反复消耗），
/// 但**不会漏文件**。这是方案 §2.4 明确选择的 fail-closed 方向（「漏掉文件不允许」）。
/// <b>逃生通道</b>是体检层给出 <c>ManualRebuildRequired</c> / 人工重建（方案 §2.5「checkpoint 损坏/不可读」
/// 与 §3.5 体检层），必要时由人排除该文件的可读性问题；
/// <b>不得</b>用「允许推进」来绕过它（那就是把永久丢失数据当成解决方案）。
/// </para>
/// <para>
/// <b>边界（本类只回答一件事）</b>：本类只根据「结果计数」判定是否允许推进。
/// 「这是否是一个**完整补偿轮次**」是调用方（S3 执行层）的前置条件，不在本接缝的判定范围内；
/// <c>FullTextMutationResult.CheckpointAdvanced</c> 字段是本判定的**产物**，因此本类**不回读**它
/// （回读会形成循环依赖，也会让一个陈旧/谎报的标志覆盖真实计数）。
/// </para>
/// <para>
/// 本类**不含** SHA256、路径命名哈希、噪声名单（命名哈希的唯一真源是 <c>FullTextIndexPaths</c>），
/// 也不读写任何文件、不创建目录。
/// </para>
/// </summary>
public static class CheckpointAdvancePolicy
{
    /// <summary>
    /// 本轮是否允许推进 checkpoint（判定核心，语义见类注释）。
    /// </summary>
    /// <param name="result">本批局部写入的结果。</param>
    /// <returns>true ⇒ 允许把 watermark 推进到本轮扫描开始时刻。</returns>
    public static bool AllowsAdvance(FullTextMutationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.State == FullTextMutationState.Applied
            && result.FailedCount == 0
            && result.RetainedOldCount == 0;
    }

    /// <summary>
    /// 判定是否允许推进，并给出**可区分**的阻止原因（失败 / 取消 / 预算拒绝 / 互斥 / 保留待重试）。
    /// </summary>
    /// <param name="result">本批局部写入的结果。</param>
    public static CheckpointAdvanceDecision Decide(FullTextMutationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return AllowsAdvance(result)
            ? CheckpointAdvanceDecision.Allow
            : CheckpointAdvanceDecision.Block(DescribeBlockReason(result));
    }

    /// <summary>
    /// 推导阻止原因。仅在 <see cref="AllowsAdvance"/> 为 false 时使用：
    /// 先按终态归类（失败 / 取消 / 预算拒绝 / 互斥 / 部分成功），再处理「Applied 但计数不为 0」的防御分支。
    /// </summary>
    private static CheckpointAdvanceBlockReason DescribeBlockReason(FullTextMutationResult result)
    {
        if (result.State != FullTextMutationState.Applied)
        {
            return result.State switch
            {
                FullTextMutationState.PartiallyApplied => CheckpointAdvanceBlockReason.PartiallyApplied,
                FullTextMutationState.Rejected => CheckpointAdvanceBlockReason.Rejected,
                FullTextMutationState.Busy => CheckpointAdvanceBlockReason.Busy,
                FullTextMutationState.Cancelled => CheckpointAdvanceBlockReason.Cancelled,
                FullTextMutationState.Failed => CheckpointAdvanceBlockReason.Failed,

                // 防御：新增终态而未同步本策略时按 fail-closed 处理（绝不默认放行）。
                _ => CheckpointAdvanceBlockReason.StateNotApplied,
            };
        }

        if (result.FailedCount > 0)
            return CheckpointAdvanceBlockReason.FailedItemsPresent;

        if (result.RetainedOldCount > 0)
            return CheckpointAdvanceBlockReason.RetainedOldItemsPresent;

        // 不可达：AllowsAdvance 为 false 时上面三条必有一条命中。
        return CheckpointAdvanceBlockReason.StateNotApplied;
    }
}
