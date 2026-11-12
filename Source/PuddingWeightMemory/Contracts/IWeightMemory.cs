namespace PuddingWeightMemory;

/// <summary>
/// 召回与学习的唯一入口。
/// <para>
/// 所有权：组件拥有每个分区的章节参数与快照指针；调用方只在轮次边界调用一次
/// <see cref="PredictAsync"/>，并把返回的快照号与原样命中一起落盘，再据此提交反馈。
/// 三个成员都不做任何跨分区读取，也不相信调用方之外的任何身份来源。
/// </para>
/// </summary>
public interface IWeightMemory
{
    /// <summary>
    /// 在指定（或当前活动）快照上对分区内可见章节打分。只读：不修改权重、不推进快照、
    /// 不写入事件回执。
    /// </summary>
    /// <param name="query">本轮召回请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>按分数降序的命中；最高分或第一二名分差未过阈值时为空。</returns>
    Task<RecallResult> PredictAsync(RecallQuery query, CancellationToken ct);

    /// <summary>
    /// 登记一个可召回的章节地址。重复的 <c>(Scope, ChapterId, EventId)</c> 是幂等空操作。
    /// </summary>
    /// <param name="chapter">已核验的保存回执。</param>
    /// <param name="ct">取消令牌。</param>
    Task RegisterAsync(SavedChapter chapter, CancellationToken ct);

    /// <summary>
    /// 用一条可核对反馈推进**候选**版本权重。活动快照不因反馈变化；
    /// 同一 <c>EventId</c>（或同一消息对同一章节）重复提交不重复训练。
    /// </summary>
    /// <param name="feedback">已核验的反馈。</param>
    /// <param name="ct">取消令牌。</param>
    Task ApplyFeedbackAsync(Feedback feedback, CancellationToken ct);
}
