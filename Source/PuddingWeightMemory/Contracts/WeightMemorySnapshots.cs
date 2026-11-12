namespace PuddingWeightMemory;

/// <summary>
/// 一个分区的两个可寻址版本。
/// <para>
/// <see cref="ActiveSnapshotId"/> 是预测实际读取的版本；<see cref="CandidateSnapshotId"/>
/// 是反馈在推进的版本。两者在登记时内容相同，之后只由 <c>ApplyFeedbackAsync</c> 拉开差距，
/// 直到发生一次发布。
/// </para>
/// </summary>
/// <param name="ActiveSnapshotId">当前活动快照。</param>
/// <param name="CandidateSnapshotId">当前候选快照。</param>
public sealed record WeightMemorySnapshots(string ActiveSnapshotId, string CandidateSnapshotId);
