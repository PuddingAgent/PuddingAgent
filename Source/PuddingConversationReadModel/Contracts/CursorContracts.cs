namespace PuddingConversationReadModel;

/// <summary>
/// 事件最新位置 H（已提交事件）。只用于表达同步差额，不得当作读模型快照游标（Plan §4.2）。
/// </summary>
public readonly record struct EventHead(string ConversationId, long Sequence);

/// <summary>响应所覆盖的读模型位置 C；初次 SSE 以 afterSequence=C 续接。约束 C &lt;= H。</summary>
public readonly record struct SnapshotCursor(string ConversationId, long Sequence);

/// <summary>客户端按顺序应用到的位置 A；必须与本地状态一起持久化，断线重连使用。</summary>
public readonly record struct LastAppliedSequence(string ConversationId, long Sequence);

/// <summary>游标合同校验失败原因；按枚举顺序报告第一个失败（顺序在 XML 注释中冻结）。</summary>
public enum CursorContractFailure
{
    None = 0,
    EmptyConversationId = 1,
    ConversationMismatch = 2,
    NegativeSequence = 3,
    SnapshotAheadOfHead = 4,
    AppliedAheadOfHead = 5,
}

/// <summary>
/// 三种游标的合同校验（Plan §4.2）。校验顺序（冻结）：
/// <list type="number">
/// <item>任一会话 id 为空 ⇒ <see cref="CursorContractFailure.EmptyConversationId"/>；</item>
/// <item>三者会话 id 不一致 ⇒ <see cref="CursorContractFailure.ConversationMismatch"/>；</item>
/// <item>任一 sequence &lt; 0 ⇒ <see cref="CursorContractFailure.NegativeSequence"/>；</item>
/// <item>snapshot &gt; head ⇒ <see cref="CursorContractFailure.SnapshotAheadOfHead"/>；</item>
/// <item>lastApplied 非空且 lastApplied &gt; head ⇒ <see cref="CursorContractFailure.AppliedAheadOfHead"/>。</item>
/// </list>
/// 通过返回 <see cref="CursorContractFailure.None"/>。
/// </summary>
public static class CursorContract
{
    public static CursorContractFailure Validate(
        EventHead head,
        SnapshotCursor snapshot,
        LastAppliedSequence? lastApplied = null)
        => CursorRules.Validate(head, snapshot, lastApplied);
}

/// <summary>历史消息键集游标：绑定会话、稳定排序版本与 (createdAt, id) 页边界（Plan §5.2）。</summary>
public sealed record HistoryPageCursor(
    string ConversationId,
    DateTimeOffset BeforeCreatedAt,
    string BeforeMessageId,
    int SortVersion,
    int Limit);

/// <summary>过程明细游标：按 canonical sequence 向更早翻页，并固定一次读取的 through 上界。</summary>
public sealed record ProcessPageCursor(
    string ConversationId,
    string? TurnId,
    long BeforeSequence,
    long ThroughSequence,
    int Limit);

/// <summary>分页游标校验失败原因；按枚举顺序报告第一个失败（顺序在 XML 注释中冻结）。</summary>
public enum PageCursorFailure
{
    None = 0,
    EmptyConversationId = 1,
    ConversationMismatch = 2,
    EmptyMessageIdentity = 3,
    SortVersionMismatch = 4,
    TurnMismatch = 5,
    NegativeSequence = 6,
    SequenceOutOfRange = 7,
    InvalidLimit = 8,
}

/// <summary>
/// 历史/过程分页游标合同校验（Plan §5.2、§5.3）。校验顺序（冻结）：
/// <list type="number">
/// <item>cursor/expected 会话为空 ⇒ EmptyConversationId；cursor 与 expected 不一致 ⇒ ConversationMismatch；</item>
/// <item>历史：BeforeMessageId 为空 ⇒ EmptyMessageIdentity；SortVersion 与 expected 不一致 ⇒ SortVersionMismatch；</item>
/// <item>过程：expectedTurnId 非空且与 cursor.TurnId 不一致 ⇒ TurnMismatch；</item>
/// <item>序列为负 ⇒ NegativeSequence；过程要求 BeforeSequence &gt; ThroughSequence+1 ⇒ SequenceOutOfRange；</item>
/// <item>Limit &lt;= 0 或超过对应页硬上限（历史 MaxOpenMessageLimit / 过程 MaxDetailPageItems）⇒ InvalidLimit。</item>
/// </list>
/// 通过返回 <see cref="PageCursorFailure.None"/>；空 TurnId 表示跨 Turn 查询，不做 Turn 匹配。
/// </summary>
public static class PageCursorContract
{
    public static PageCursorFailure ValidateHistory(
        HistoryPageCursor cursor,
        string expectedConversationId,
        int expectedSortVersion,
        ConversationReadModelLimits? limits = null)
        => CursorRules.ValidateHistory(cursor, expectedConversationId, expectedSortVersion, limits ?? ConversationReadModelLimits.Default);

    public static PageCursorFailure ValidateProcess(
        ProcessPageCursor cursor,
        string expectedConversationId,
        string? expectedTurnId,
        long eventHeadSequence,
        ConversationReadModelLimits? limits = null)
        => CursorRules.ValidateProcess(cursor, expectedConversationId, expectedTurnId, eventHeadSequence, limits ?? ConversationReadModelLimits.Default);
}
