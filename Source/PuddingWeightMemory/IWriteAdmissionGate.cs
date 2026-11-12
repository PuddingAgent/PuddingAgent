namespace PuddingWeightMemory;

/// <summary>
/// 写入准入：决定"现在还有没有余量做一次训练"。
/// <para>
/// 所有权：这是"过载时宁可丢观测也不拖住主流程"这一策略的落点。影子期一次漏测只影响统计，
/// 而排队等待会把延迟加到用户回合上，所以队列满时登记的章节不写、反馈不训、预测返回空结果，
/// 三种情况都不抛异常，只增加 <see cref="WeightMemoryCounters"/> 里的降级计数。
/// </para>
/// <para>
/// 实现约束：<see cref="TryEnter"/> 必须是非阻塞的；成功进入后无论成败都必须调用 <see cref="Exit"/>。
/// </para>
/// </summary>
public interface IWriteAdmissionGate
{
    /// <summary>队列容量。</summary>
    int Capacity { get; }

    /// <summary>当前占用中的写入槽数。</summary>
    int PendingWrites { get; }

    /// <summary>是否已无空槽（预测据此降级为空结果）。</summary>
    bool IsSaturated { get; }

    /// <summary>非阻塞地占用一个写入槽。</summary>
    /// <returns>占用成功返回 <c>true</c>；已满返回 <c>false</c>，调用方必须放弃本次写入。</returns>
    bool TryEnter();

    /// <summary>归还一个写入槽。</summary>
    void Exit();
}
