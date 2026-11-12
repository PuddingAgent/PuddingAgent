namespace PuddingWeightMemory;

/// <summary>
/// 一条可核对的用户反馈：某句话应该想起哪些章节。
/// <para>
/// 所有权：这是"人类确认过这个说法指向哪一章"这一事实。宿主负责判定它够格当监督信号
/// （用户点名、确认或更正，且有展示凭据）；组件只负责把这句 cue 学进候选权重。
/// </para>
/// <para>
/// <see cref="AcceptableChapterIds"/> 是"都算对"的集合：这些章节一律不进困难负例，
/// 否则会把另一个正确答案当成反例推开。
/// </para>
/// </summary>
/// <param name="Scope">分区。</param>
/// <param name="Source">反馈所在的轮次。</param>
/// <param name="EventId">反馈事件标识，幂等键。</param>
/// <param name="PrimaryChapterId">首选正确章节。</param>
/// <param name="AcceptableChapterIds">其他同样可接受的章节。</param>
/// <param name="Cue">这轮用户原文，编码后作为训练 cue。</param>
public sealed record Feedback(
    MemoryScope Scope,
    TurnKey Source,
    string EventId,
    string PrimaryChapterId,
    IReadOnlyList<string> AcceptableChapterIds,
    string Cue);
