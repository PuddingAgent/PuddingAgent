namespace PuddingWeightMemory;

/// <summary>
/// 一条已提交的"章节已保存"回执，用于登记可召回的地址。
/// <para>
/// 所有权：这是"某个 save_memory 回执确实落库了"这一事实。宿主负责从会话事件库配对请求与完成
/// 事件、核验用户/工作区/Agent 与章节归属；组件只接受核验后的结果。
/// </para>
/// <para>
/// <see cref="SourceCue"/> 是登记时的那句用户话（或章节线索原文）。它是本次切片唯一可编码的
/// 文本：合同里没有章节正文，因此章节的初始权重就是这句线索的归一化向量，同时作为后续训练的
/// 锚点（反馈训练不允许权重离它太远）。
/// </para>
/// </summary>
/// <param name="Scope">分区。</param>
/// <param name="Source">产生这次保存的轮次。</param>
/// <param name="EventId">回执事件标识，幂等键。</param>
/// <param name="BookId">章节所属记忆书。</param>
/// <param name="ChapterId">章节标识。</param>
/// <param name="SourceCue">登记线索原文。</param>
public sealed record SavedChapter(
    MemoryScope Scope,
    TurnKey Source,
    string EventId,
    string BookId,
    string ChapterId,
    string SourceCue);
