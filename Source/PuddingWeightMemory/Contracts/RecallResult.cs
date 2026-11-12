namespace PuddingWeightMemory;

/// <summary>
/// 一次召回的完整结果。
/// <para>
/// <see cref="SnapshotId"/> 是实际读取的快照，必须随预测一并落盘：反馈到达后要与"当时那个快照"
/// 配对，否则无法判断这次命中是冻结组还是训练组的功劳。<see cref="Hits"/> 为空表示
/// 没有通过阈值的地址（含降级），调用方不得据此推断"库里没有这一章"。
/// </para>
/// </summary>
/// <param name="SnapshotId">实际读取的快照标识；分区尚未建立时为空字符串。</param>
/// <param name="Hits">按分数降序的命中，同分按章节 ID 序数序。</param>
public sealed record RecallResult(string SnapshotId, IReadOnlyList<MemoryHit> Hits);
