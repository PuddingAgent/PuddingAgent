using System.Text;

namespace PuddingConversationReadModelTests;

/// <summary>
/// 测试数据工厂与状态断言辅助。只使用组件自身的公开/内部 API，不引用任何宿主类型。
/// </summary>
internal static class TestData
{
    internal const string Conversation = "conv-1";
    internal const string ForeignConversation = "conv-2";

    internal const string RootRunId = "run-root-1";
    internal const string RootTurnId = "turn-1";
    internal const string SubAgentRunId = "run-child-1";
    internal const string UserMessageId = "user-msg-1";
    internal const string AssistantMessageId = "assistant-msg-1";

    // ---------------------------------------------------------------- 事件工厂

    internal static ConversationEventInput Event(
        long sequence,
        ConversationEventKind kind,
        string? turnId = null,
        string? runId = null,
        string? parentRunId = null,
        string? messageId = null,
        string? subAgentId = null,
        string? textDelta = null,
        int payloadBytes = 0,
        string? conversationId = null,
        string? eventId = null) => new()
    {
        ConversationId = conversationId ?? Conversation,
        Sequence = sequence,
        EventId = eventId ?? $"evt-{sequence}",
        Kind = kind,
        TurnId = turnId,
        RunId = runId,
        ParentRunId = parentRunId,
        MessageId = messageId,
        SubAgentId = subAgentId,
        TextDelta = textDelta,
        PayloadBytes = payloadBytes,
    };

    internal static ConversationEventInput TurnStarted(
        long sequence,
        string turnId = RootTurnId,
        string? runId = RootRunId,
        string? userMessageId = UserMessageId,
        string? parentRunId = null)
        => Event(sequence, ConversationEventKind.TurnStarted, turnId: turnId, runId: runId, messageId: userMessageId, parentRunId: parentRunId);

    internal static ConversationEventInput ContentAppended(long sequence, string text, string? turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.MessageContentAppended, turnId: turnId, textDelta: text);

    internal static ConversationEventInput ThinkingAppended(long sequence, string text, string? turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.MessageThinkingSummaryAppended, turnId: turnId, textDelta: text);

    internal static ConversationEventInput MessageCompleted(long sequence, string? messageId = AssistantMessageId, string? turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.MessageCompleted, turnId: turnId, messageId: messageId);

    internal static ConversationEventInput TurnCompleted(long sequence, string turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.TurnCompleted, turnId: turnId);

    internal static ConversationEventInput TurnFailed(long sequence, string turnId = RootTurnId, string? messageId = null)
        => Event(sequence, ConversationEventKind.TurnFailed, turnId: turnId, messageId: messageId);

    internal static ConversationEventInput TurnCancelled(long sequence, string turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.TurnCancelled, turnId: turnId);

    internal static ConversationEventInput ToolRequested(long sequence, string? turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.ToolCallRequested, turnId: turnId);

    internal static ConversationEventInput ToolCompleted(long sequence, string? turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.ToolCallCompleted, turnId: turnId);

    internal static ConversationEventInput ToolFailed(long sequence, string? turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.ToolCallFailed, turnId: turnId);

    internal static ConversationEventInput SubAgentCreated(long sequence, string? turnId = RootTurnId)
        => Event(sequence, ConversationEventKind.SubAgentRunCreated, turnId: turnId, runId: SubAgentRunId, parentRunId: RootRunId, subAgentId: "sub-agent-1");

    internal static ConversationEventInput SubAgentLifecycle(
        long sequence,
        ConversationEventKind kind,
        string? turnId = RootTurnId,
        string runId = SubAgentRunId,
        string? parentRunId = RootRunId)
        => Event(sequence, kind, turnId: turnId, runId: runId, parentRunId: parentRunId, subAgentId: "sub-agent-1");

    internal static ConversationEventInput CompactionStarted(long sequence, string? runId = "compact-run-1")
        => Event(sequence, ConversationEventKind.ContextCompactionStarted, runId: runId);

    internal static ConversationEventInput CompactionCompleted(long sequence, string? runId = "compact-run-1")
        => Event(sequence, ConversationEventKind.ContextCompactionCompleted, runId: runId);

    internal static ConversationEventInput CompactionFailed(long sequence, string? runId = "compact-run-1")
        => Event(sequence, ConversationEventKind.ContextCompactionFailed, runId: runId);

    // ---------------------------------------------------------------- 状态工厂

    internal static ConversationReadModelState EmptyState()
        => ConversationReadModelReducer.CreateEmpty(Conversation);

    internal static ReduceBatchResult Apply(params ConversationEventInput[] events)
        => ConversationReadModelReducer.Apply(EmptyState(), events);

    internal static ReduceBatchResult Apply(ConversationReadModelState state, params ConversationEventInput[] events)
        => ConversationReadModelReducer.Apply(state, events);

    internal static ConversationReadModelState Reduced(params ConversationEventInput[] events)
        => Apply(events).State;

    // ---------------------------------------------------------------- 断言辅助

    internal static int Utf8Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    internal static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));

    /// <summary>断言文本是合法 UTF-8 往返结果：不含 U+FFFD（截断不得把码点/代理对切成替换字符）。</summary>
    internal static void AssertNoReplacementCharacters(string value, string? message = null)
    {
        foreach (var rune in value.EnumerateRunes())
            Assert.AreNotEqual(0xFFFD, rune.Value, message ?? "text contains U+FFFD replacement character");
    }

    internal static void AssertTurnEquivalent(TurnReadSummary expected, TurnReadSummary actual)
    {
        Assert.AreEqual(expected.TurnId, actual.TurnId);
        Assert.AreEqual(expected.RootRunId, actual.RootRunId);
        Assert.AreEqual(expected.UserMessageId, actual.UserMessageId);
        Assert.AreEqual(expected.AssistantMessageId, actual.AssistantMessageId);
        Assert.AreEqual(expected.TerminalState, actual.TerminalState);
        Assert.AreEqual(expected.HasAssistantBody, actual.HasAssistantBody);
        Assert.AreEqual(expected.ActivePreview, actual.ActivePreview);
        Assert.AreEqual(expected.ActivePreviewTruncated, actual.ActivePreviewTruncated);
        Assert.AreEqual(expected.FirstSequence, actual.FirstSequence);
        Assert.AreEqual(expected.LastSequence, actual.LastSequence);
        Assert.AreEqual(expected.Revision, actual.Revision);
        Assert.AreEqual(expected.Process.ContentAppendCount, actual.Process.ContentAppendCount);
        Assert.AreEqual(expected.Process.ThinkingAppendCount, actual.Process.ThinkingAppendCount);
        Assert.AreEqual(expected.Process.ToolCallRequestedCount, actual.Process.ToolCallRequestedCount);
        Assert.AreEqual(expected.Process.ToolCallCompletedCount, actual.Process.ToolCallCompletedCount);
        Assert.AreEqual(expected.Process.ToolCallFailedCount, actual.Process.ToolCallFailedCount);
        Assert.AreEqual(expected.Process.SubAgentRunCount, actual.Process.SubAgentRunCount);
        Assert.AreEqual(expected.Process.Completeness, actual.Process.Completeness);
        Assert.AreEqual(expected.Process.IncompleteReason, actual.Process.IncompleteReason);
        Assert.AreEqual(expected.Process.Revision, actual.Process.Revision);
    }

    internal static void AssertTurnsEquivalent(ConversationReadModelState expected, ConversationReadModelState actual)
    {
        Assert.HasCount(expected.Turns.Count, actual.Turns);
        foreach (var (turnId, expectedTurn) in expected.Turns)
        {
            Assert.IsTrue(actual.Turns.TryGetValue(turnId, out var actualTurn), $"turn '{turnId}' is missing");
            AssertTurnEquivalent(expectedTurn, actualTurn!);
        }
    }

    internal static void AssertCompactionEquivalent(CompactionReadState? expected, CompactionReadState? actual)
    {
        if (expected is null)
        {
            Assert.IsNull(actual);
            return;
        }

        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.IsRunning, actual!.IsRunning);
        Assert.AreEqual(expected.RunId, actual.RunId);
        Assert.AreEqual(expected.StartedSequence, actual.StartedSequence);
        Assert.AreEqual(expected.CompletedSequence, actual.CompletedSequence);
        Assert.AreEqual(expected.LastAttemptFailed, actual.LastAttemptFailed);
        Assert.AreEqual(expected.Revision, actual.Revision);
    }

    internal static void AssertSubAgentRunsEquivalent(ConversationReadModelState expected, ConversationReadModelState actual)
    {
        Assert.HasCount(expected.SubAgentRuns.Count, actual.SubAgentRuns);
        Assert.AreEqual(expected.SubAgentRunsOmitted, actual.SubAgentRunsOmitted);
        for (var i = 0; i < expected.SubAgentRuns.Count; i++)
        {
            var e = expected.SubAgentRuns[i];
            var a = actual.SubAgentRuns[i];
            Assert.AreEqual(e.RunId, a.RunId);
            Assert.AreEqual(e.SubAgentId, a.SubAgentId);
            Assert.AreEqual(e.ParentRunId, a.ParentRunId);
            Assert.AreEqual(e.TurnId, a.TurnId);
            Assert.AreEqual(e.State, a.State);
            Assert.AreEqual(e.FirstSequence, a.FirstSequence);
            Assert.AreEqual(e.LastSequence, a.LastSequence);
            Assert.AreEqual(e.IsActive, a.IsActive);
        }
    }

    /// <summary>逐字段比较两份读模型状态（不含集合引用相等假设）。</summary>
    internal static void AssertStateEquivalent(ConversationReadModelState expected, ConversationReadModelState actual)
    {
        Assert.AreEqual(expected.ConversationId, actual.ConversationId);
        Assert.AreEqual(expected.ProjectedThroughSequence, actual.ProjectedThroughSequence);
        Assert.AreEqual(expected.ProjectedThroughEventId, actual.ProjectedThroughEventId);
        Assert.AreEqual(expected.Initialization, actual.Initialization);
        Assert.AreEqual(expected.CurrentTurnId, actual.CurrentTurnId);
        Assert.AreEqual(expected.CurrentRootRunId, actual.CurrentRootRunId);
        Assert.AreEqual(expected.CurrentRootTerminalState, actual.CurrentRootTerminalState);
        Assert.AreEqual(expected.ReadModelVersion, actual.ReadModelVersion);
        Assert.AreEqual(expected.Revision, actual.Revision);
        AssertTurnsEquivalent(expected, actual);
        AssertCompactionEquivalent(expected.Compaction, actual.Compaction);
        AssertSubAgentRunsEquivalent(expected, actual);
    }
}
