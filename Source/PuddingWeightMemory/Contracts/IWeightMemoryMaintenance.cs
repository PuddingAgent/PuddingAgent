namespace PuddingWeightMemory;

/// <summary>
/// 运维侧操作：失效与快照发布。
/// <para>
/// 所有权：发布节奏由外部评估决定（影子期用冻结组与候选组配对计分），组件只提供
/// "按预期旧版本原子切换"的能力，自己不判断"训练是否变好了"。
/// </para>
/// </summary>
public interface IWeightMemoryMaintenance
{
    /// <summary>
    /// 使某章节在**所有**快照（活动、候选与历史）上立即不可见。幂等键是事件标识。
    /// </summary>
    /// <param name="scope">分区。</param>
    /// <param name="chapterId">章节标识。</param>
    /// <param name="eventId">删除回执事件标识。</param>
    /// <param name="ct">取消令牌。</param>
    Task InvalidateChapterAsync(MemoryScope scope, string chapterId, string eventId, CancellationToken ct);

    /// <summary>
    /// 比较交换：仅当当前活动快照等于 <paramref name="expectedActiveSnapshotId"/> 时，
    /// 把 <paramref name="evaluatedCandidateSnapshotId"/> 切为活动快照，并据它派生一个新的候选快照。
    /// 传旧快照号即为回滚。任一前置条件不满足都返回 <c>false</c> 且不改任何状态。
    /// </summary>
    /// <param name="scope">分区。</param>
    /// <param name="expectedActiveSnapshotId">调用方看到的当前活动快照。</param>
    /// <param name="evaluatedCandidateSnapshotId">已完成评估、可发布的候选快照。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>切换是否发生。</returns>
    Task<bool> TryPublishAsync(
        MemoryScope scope,
        string expectedActiveSnapshotId,
        string evaluatedCandidateSnapshotId,
        CancellationToken ct);

    /// <summary>
    /// 读取分区的活动与候选快照号；分区尚未建立时返回 <c>null</c>。
    /// <para>
    /// 这是冻结合同里缺的一环：配对计分要同时钉住冻结组与候选组，
    /// <see cref="TryPublishAsync"/> 也需要调用方手里有候选快照号，而预测只回活动快照号。
    /// </para>
    /// </summary>
    /// <param name="scope">分区。</param>
    /// <param name="ct">取消令牌。</param>
    Task<WeightMemorySnapshots?> ReadSnapshotsAsync(MemoryScope scope, CancellationToken ct);
}
