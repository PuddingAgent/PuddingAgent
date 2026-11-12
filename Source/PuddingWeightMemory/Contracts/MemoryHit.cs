namespace PuddingWeightMemory;

/// <summary>
/// 一条召回命中：章节标识与点积分数。
/// <para>
/// 所有权：这是"这个快照认为该想起哪一章"这一事实。分数是归一化查询向量与章节向量的点积，
/// 落在 [-1, 1]；它只在同一快照内可比，不跨快照、不跨编码器解释。
/// </para>
/// </summary>
/// <param name="ChapterId">被命中的章节标识。</param>
/// <param name="Score">点积分数。</param>
public sealed record MemoryHit(string ChapterId, float Score);
