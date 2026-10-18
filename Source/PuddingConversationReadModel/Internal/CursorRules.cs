namespace PuddingConversationReadModel;

/// <summary>
/// 游标合同校验的内部实现：严格按冻结合同 XML 文档中固定的校验顺序报告第一个失败原因。
/// </summary>
internal static class CursorRules
{
    internal static CursorContractFailure Validate(
        EventHead head,
        SnapshotCursor snapshot,
        LastAppliedSequence? lastApplied)
    {
        // 冻结顺序 1：任一会话 id 为空。
        if (IsEmpty(head.ConversationId)
            || IsEmpty(snapshot.ConversationId)
            || (lastApplied.HasValue && IsEmpty(lastApplied.Value.ConversationId)))
        {
            return CursorContractFailure.EmptyConversationId;
        }

        // 冻结顺序 2：三者会话 id 不一致。
        if (!string.Equals(head.ConversationId, snapshot.ConversationId, StringComparison.Ordinal)
            || (lastApplied.HasValue
                && !string.Equals(head.ConversationId, lastApplied.Value.ConversationId, StringComparison.Ordinal)))
        {
            return CursorContractFailure.ConversationMismatch;
        }

        // 冻结顺序 3：任一 sequence < 0。
        if (head.Sequence < 0
            || snapshot.Sequence < 0
            || (lastApplied.HasValue && lastApplied.Value.Sequence < 0))
        {
            return CursorContractFailure.NegativeSequence;
        }

        // 冻结顺序 4：snapshot > head。
        if (snapshot.Sequence > head.Sequence)
            return CursorContractFailure.SnapshotAheadOfHead;

        // 冻结顺序 5：lastApplied > head。
        if (lastApplied.HasValue && lastApplied.Value.Sequence > head.Sequence)
            return CursorContractFailure.AppliedAheadOfHead;

        return CursorContractFailure.None;
    }

    internal static PageCursorFailure ValidateHistory(
        HistoryPageCursor cursor,
        string expectedConversationId,
        int expectedSortVersion,
        ConversationReadModelLimits limits)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (IsEmpty(cursor.ConversationId) || IsEmpty(expectedConversationId))
            return PageCursorFailure.EmptyConversationId;

        if (!string.Equals(cursor.ConversationId, expectedConversationId, StringComparison.Ordinal))
            return PageCursorFailure.ConversationMismatch;

        if (IsEmpty(cursor.BeforeMessageId))
            return PageCursorFailure.EmptyMessageIdentity;

        if (cursor.SortVersion != expectedSortVersion)
            return PageCursorFailure.SortVersionMismatch;

        if (cursor.Limit <= 0 || cursor.Limit > limits.MaxOpenMessageLimit)
            return PageCursorFailure.InvalidLimit;

        return PageCursorFailure.None;
    }

    internal static PageCursorFailure ValidateProcess(
        ProcessPageCursor cursor,
        string expectedConversationId,
        string? expectedTurnId,
        long eventHeadSequence,
        ConversationReadModelLimits limits)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (IsEmpty(cursor.ConversationId) || IsEmpty(expectedConversationId))
            return PageCursorFailure.EmptyConversationId;

        if (!string.Equals(cursor.ConversationId, expectedConversationId, StringComparison.Ordinal))
            return PageCursorFailure.ConversationMismatch;

        // expectedTurnId 为空表示跨 Turn 查询，不做 Turn 匹配。
        if (!IsEmpty(expectedTurnId)
            && !string.Equals(cursor.TurnId, expectedTurnId, StringComparison.Ordinal))
        {
            return PageCursorFailure.TurnMismatch;
        }

        if (cursor.BeforeSequence < 0 || cursor.ThroughSequence < 0)
            return PageCursorFailure.NegativeSequence;

        // through 不得越过调用方固定的事件 head；before 最多到 through+1（空页且已耗尽）。
        if (cursor.ThroughSequence > eventHeadSequence)
            return PageCursorFailure.SequenceOutOfRange;
        if (cursor.BeforeSequence - 1 > cursor.ThroughSequence)
            return PageCursorFailure.SequenceOutOfRange;

        if (cursor.Limit <= 0 || cursor.Limit > limits.MaxDetailPageItems)
            return PageCursorFailure.InvalidLimit;

        return PageCursorFailure.None;
    }

    private static bool IsEmpty(string? value) => string.IsNullOrWhiteSpace(value);
}
