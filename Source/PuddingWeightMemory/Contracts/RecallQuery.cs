namespace PuddingWeightMemory;

/// <summary>
/// 一次只读召回请求。
/// <para>
/// 所有权：调用方声明"谁、在哪个快照上、用哪句话找记忆"。<see cref="SnapshotId"/> 为空表示
/// 取当前活动快照；非空表示钉住该快照（离线回放必须钉住，避免拿今天的更正回答昨天的问句）。
/// </para>
/// <para>
/// <see cref="PriorTurns"/> 是同一轮对话里此前被展示过的原文，随请求留痕；本切片的打分只用
/// <see cref="CurrentText"/>，因为实验的定案打分式就是"归一化查询向量与章节向量的点积"。
/// </para>
/// </summary>
/// <param name="Scope">分区。</param>
/// <param name="Turn">本轮身份。</param>
/// <param name="SnapshotId">要读的快照；空字符串表示当前活动快照。</param>
/// <param name="CurrentText">本轮用户原文，编码后作为查询向量。</param>
/// <param name="PriorTurns">此前展示过的原文，按时间升序。</param>
/// <param name="Limit">最多返回多少条命中；小于等于 0 视为空结果。</param>
public sealed record RecallQuery(
    MemoryScope Scope,
    TurnKey Turn,
    string SnapshotId,
    string CurrentText,
    IReadOnlyList<string> PriorTurns,
    int Limit);
