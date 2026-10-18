#pragma warning disable MSTEST0032 // frozen contract constant assertions are intentional

namespace PuddingConversationReadModelTests;

/// <summary>
/// S3 归约规则测试（Plan §4.1；冻结合同 <see cref="ConversationReadModelReducer"/> 的 XML 文档）。
/// 覆盖：初始化、按序应用、重放幂等、倒序/跳号、跨会话拒绝、根/子 run 隔离、终态与迟到事件、
/// 正文预览有界尾部、计数、压缩状态、游标推进。
/// </summary>
[TestClass]
public sealed class ConversationReadModelReducerTests
{
    // ============================================================ CreateEmpty

    [TestMethod]
    public void CreateEmpty_InitializesReaderSafeDefaults()
    {
        var state = ConversationReadModelReducer.CreateEmpty(TestData.Conversation);

        Assert.AreEqual(TestData.Conversation, state.ConversationId);
        Assert.AreEqual(0L, state.ProjectedThroughSequence);
        Assert.IsNull(state.ProjectedThroughEventId);
        Assert.AreEqual(ReadModelInitializationState.Ready, state.Initialization);
        Assert.IsEmpty(state.Turns);
        Assert.IsNull(state.CurrentTurnId);
        Assert.IsNull(state.CurrentRootRunId);
        Assert.AreEqual(TurnTerminalState.Unknown, state.CurrentRootTerminalState);
        Assert.IsNull(state.Compaction);
        Assert.IsEmpty(state.SubAgentRuns);
        Assert.AreEqual(ConversationReadModelLimits.CurrentReadModelVersion, state.ReadModelVersion);
        Assert.AreEqual(1, ConversationReadModelLimits.CurrentReadModelVersion);
        Assert.AreEqual(0, state.Revision);
    }

    [TestMethod]
    public void CreateEmpty_PreservesRequestedInitializationState()
    {
        foreach (var initialization in new[]
                 {
                     ReadModelInitializationState.Uninitialized,
                     ReadModelInitializationState.BaselineBuilding,
                     ReadModelInitializationState.Partial,
                     ReadModelInitializationState.Ready,
                 })
        {
            var state = ConversationReadModelReducer.CreateEmpty(TestData.Conversation, initialization);
            Assert.AreEqual(initialization, state.Initialization);
        }
    }

    [TestMethod]
    public void CreateEmpty_RejectsNullConversationId()
        => Assert.ThrowsExactly<ArgumentNullException>(() => ConversationReadModelReducer.CreateEmpty(null!));

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void CreateEmpty_RejectsEmptyConversationId(string conversationId)
        => Assert.ThrowsExactly<ArgumentException>(() => ConversationReadModelReducer.CreateEmpty(conversationId));

    [TestMethod]
    public void Apply_RejectsNullState()
        => Assert.ThrowsExactly<ArgumentNullException>(
            () => ConversationReadModelReducer.Apply(null!, Array.Empty<ConversationEventInput>()));

    [TestMethod]
    public void Apply_RejectsNullBatch()
        => Assert.ThrowsExactly<ArgumentNullException>(
            () => ConversationReadModelReducer.Apply(TestData.EmptyState(), null!));

    [TestMethod]
    public void Apply_EmptyBatch_DoesNotChangeProjection()
    {
        var state = TestData.EmptyState();
        var result = ConversationReadModelReducer.Apply(state, Array.Empty<ConversationEventInput>());

        Assert.AreEqual(0, result.AppliedEventCount);
        Assert.AreEqual(0, result.DuplicateEventCount);
        Assert.AreEqual(0, result.OutOfOrderEventCount);
        Assert.AreEqual(0, result.ForeignConversationCount);
        Assert.AreEqual(0, result.LateAfterTerminalCount);
        Assert.AreEqual(0, result.IgnoredEventCount);
        Assert.IsFalse(result.ReplayDetected);
        Assert.AreEqual(0L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(0, result.State.Revision);
        Assert.IsEmpty(result.State.Turns);
    }

    // ============================================================ 基础应用与顺序

    [TestMethod]
    public void Apply_TurnStarted_CreatesTurnAndRootRun()
    {
        var result = TestData.Apply(TestData.TurnStarted(1));

        Assert.AreEqual(1, result.AppliedEventCount);
        Assert.AreEqual(0, result.DuplicateEventCount);
        Assert.AreEqual(0, result.OutOfOrderEventCount);
        Assert.AreEqual(0, result.ForeignConversationCount);
        Assert.AreEqual(0, result.LateAfterTerminalCount);
        Assert.AreEqual(0, result.IgnoredEventCount);
        Assert.IsFalse(result.ReplayDetected);

        var state = result.State;
        Assert.AreEqual(1L, state.ProjectedThroughSequence);
        Assert.AreEqual("evt-1", state.ProjectedThroughEventId);
        Assert.AreEqual(1, state.Revision);
        Assert.HasCount(1, state.Turns);

        var turn = state.Turns[TestData.RootTurnId];
        Assert.AreEqual(TestData.RootTurnId, turn.TurnId);
        Assert.AreEqual(TestData.RootRunId, turn.RootRunId);
        Assert.AreEqual(TestData.UserMessageId, turn.UserMessageId);
        Assert.IsNull(turn.AssistantMessageId);
        Assert.AreEqual(TurnTerminalState.Unknown, turn.TerminalState);
        Assert.IsFalse(turn.HasAssistantBody);
        Assert.AreEqual(string.Empty, turn.ActivePreview);
        Assert.IsFalse(turn.ActivePreviewTruncated);
        Assert.AreEqual(1L, turn.FirstSequence);
        Assert.AreEqual(1L, turn.LastSequence);
        Assert.AreEqual(1, turn.Revision);

        Assert.AreEqual(TestData.RootTurnId, state.CurrentTurnId);
        Assert.AreEqual(TestData.RootRunId, state.CurrentRootRunId);
        Assert.AreEqual(TurnTerminalState.Unknown, state.CurrentRootTerminalState);
    }

    [TestMethod]
    public void Apply_SortsBatchBySequenceRegardlessOfInputOrder()
    {
        var batch = new[]
        {
            TestData.ContentAppended(3, "C"),
            TestData.TurnStarted(1),
            TestData.ContentAppended(2, "B"),
        };

        var result = TestData.Apply(batch);

        Assert.AreEqual(3, result.AppliedEventCount);
        Assert.AreEqual(2, result.OutOfOrderEventCount,
            "两处输入顺序倒置（seq 3 之后出现 1、2）必须计入 OutOfOrderEventCount");
        Assert.AreEqual(3L, result.State.ProjectedThroughSequence);
        Assert.AreEqual("evt-3", result.State.ProjectedThroughEventId);

        var turn = result.State.Turns[TestData.RootTurnId];
        Assert.AreEqual("BC", turn.ActivePreview);
        Assert.AreEqual(2, turn.Process.ContentAppendCount);
        Assert.AreEqual(1L, turn.FirstSequence);
        Assert.AreEqual(3L, turn.LastSequence);
    }

    [TestMethod]
    public void Apply_ReplaySameBatch_IsIdempotent()
    {
        var batch = new[]
        {
            TestData.TurnStarted(1),
            TestData.ContentAppended(2, "hi"),
            TestData.MessageCompleted(3),
            TestData.TurnCompleted(4),
        };

        var first = TestData.Apply(batch);
        Assert.AreEqual(4, first.AppliedEventCount);
        Assert.AreEqual(1, first.State.Revision);
        Assert.IsFalse(first.ReplayDetected);

        var second = TestData.Apply(first.State, batch);

        Assert.AreEqual(0, second.AppliedEventCount);
        Assert.AreEqual(4, second.DuplicateEventCount);
        Assert.IsTrue(second.ReplayDetected);
        Assert.AreEqual(4L, second.State.ProjectedThroughSequence);
        Assert.AreEqual("evt-4", second.State.ProjectedThroughEventId);
        TestData.AssertStateEquivalent(first.State, second.State);
    }

    [TestMethod]
    public void Apply_OverlappingBatch_AppliesOnlyUnseenEvents()
    {
        var first = TestData.Apply(TestData.TurnStarted(1), TestData.ContentAppended(2, "a"));

        var second = TestData.Apply(
            first.State,
            TestData.ContentAppended(2, "a"),
            TestData.ContentAppended(3, "b"));

        Assert.AreEqual(1, second.AppliedEventCount);
        Assert.AreEqual(1, second.DuplicateEventCount);
        Assert.IsTrue(second.ReplayDetected);
        Assert.AreEqual(3L, second.State.ProjectedThroughSequence);
        Assert.AreEqual("ab", second.State.Turns[TestData.RootTurnId].ActivePreview);
    }

    [TestMethod]
    public void Apply_GapInsideBatch_LeavesGapEventsUnapplied()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.ContentAppended(3, "must-not-apply"));

        Assert.AreEqual(1, result.AppliedEventCount);
        Assert.AreEqual(1, result.OutOfOrderEventCount);
        Assert.AreEqual(1L, result.State.ProjectedThroughSequence);
        Assert.AreEqual("evt-1", result.State.ProjectedThroughEventId);

        var turn = result.State.Turns[TestData.RootTurnId];
        Assert.AreEqual(0, turn.Process.ContentAppendCount);
        Assert.AreEqual(string.Empty, turn.ActivePreview);
        Assert.AreEqual(1L, turn.LastSequence, "跳号事件不得推进 Turn 的 LastSequence");
    }

    [TestMethod]
    public void Apply_BatchStartingAfterGap_AppliesNothing()
    {
        var result = TestData.Apply(TestData.ContentAppended(2, "orphan"));

        Assert.AreEqual(0, result.AppliedEventCount);
        Assert.AreEqual(1, result.OutOfOrderEventCount);
        Assert.AreEqual(0L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(0, result.State.Revision);
        Assert.IsEmpty(result.State.Turns);
    }

    [TestMethod]
    public void Apply_RejectsForeignConversationEvents()
    {
        var first = TestData.Apply(TestData.TurnStarted(1));

        var result = TestData.Apply(
            first.State,
            TestData.Event(5, ConversationEventKind.TurnStarted, conversationId: TestData.ForeignConversation, turnId: "foreign-turn", runId: "foreign-run"),
            TestData.ContentAppended(2, "own"));

        Assert.AreEqual(1, result.ForeignConversationCount);
        Assert.AreEqual(1, result.AppliedEventCount);
        Assert.AreEqual(2L, result.State.ProjectedThroughSequence);
        Assert.HasCount(1, result.State.Turns);
        Assert.IsFalse(result.State.Turns.ContainsKey("foreign-turn"));
        Assert.AreEqual("own", result.State.Turns[TestData.RootTurnId].ActivePreview);
    }

    [TestMethod]
    public void Apply_ForeignOnlyBatch_DoesNotAdvanceProjection()
    {
        var result = TestData.Apply(
            TestData.Event(99, ConversationEventKind.MessageContentAppended, conversationId: TestData.ForeignConversation, turnId: "foreign-turn"));

        Assert.AreEqual(1, result.ForeignConversationCount);
        Assert.AreEqual(0L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(0, result.State.Revision);
    }

    // ============================================================ 根 run / 子代理隔离

    [TestMethod]
    public void Apply_SubAgentLifecycle_DoesNotStealRootRunOrTerminateTurn()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.SubAgentCreated(2),
            TestData.SubAgentLifecycle(3, ConversationEventKind.SubAgentRunStarted),
            TestData.SubAgentLifecycle(4, ConversationEventKind.SubAgentRunCompleted));

        Assert.AreEqual(4, result.AppliedEventCount);

        var state = result.State;
        Assert.AreEqual(TestData.RootRunId, state.CurrentRootRunId);
        Assert.AreEqual(TurnTerminalState.Unknown, state.CurrentRootTerminalState);

        var turn = state.Turns[TestData.RootTurnId];
        Assert.AreEqual(TestData.RootRunId, turn.RootRunId);
        Assert.AreEqual(TurnTerminalState.Unknown, turn.TerminalState);
        Assert.AreEqual(1, turn.Process.SubAgentRunCount);
        Assert.AreEqual(0, turn.Process.ContentAppendCount);

        Assert.HasCount(1, state.SubAgentRuns);
        var run = state.SubAgentRuns[0];
        Assert.AreEqual(TestData.SubAgentRunId, run.RunId);
        Assert.AreEqual("sub-agent-1", run.SubAgentId);
        Assert.AreEqual(TestData.RootRunId, run.ParentRunId);
        Assert.AreEqual(TestData.RootTurnId, run.TurnId);
        Assert.AreEqual(SubAgentRunLifecycleState.Completed, run.State);
        Assert.AreEqual(2L, run.FirstSequence);
        Assert.AreEqual(4L, run.LastSequence);
        Assert.IsFalse(run.IsActive);
    }

    [TestMethod]
    public void Apply_SubAgentFailure_DoesNotFailParentTurn()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.SubAgentCreated(2),
            TestData.SubAgentLifecycle(3, ConversationEventKind.SubAgentRunFailed));

        var state = result.State;
        Assert.AreEqual(TurnTerminalState.Unknown, state.CurrentRootTerminalState);
        Assert.AreEqual(TurnTerminalState.Unknown, state.Turns[TestData.RootTurnId].TerminalState);
        Assert.AreEqual(SubAgentRunLifecycleState.Failed, state.SubAgentRuns[0].State);
        Assert.AreEqual(1, state.Turns[TestData.RootTurnId].Process.SubAgentRunCount);
    }

    [TestMethod]
    public void Apply_SubAgentEventWithoutParent_StillDoesNotChangeRootRun()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.SubAgentLifecycle(2, ConversationEventKind.SubAgentRunStarted, parentRunId: null));

        Assert.AreEqual(TestData.RootRunId, result.State.CurrentRootRunId);
        Assert.HasCount(1, result.State.SubAgentRuns);
        Assert.IsNull(result.State.SubAgentRuns[0].ParentRunId);
    }

    [TestMethod]
    public void Apply_TurnStartedWithParentRunId_IsIgnoredAndDoesNotStealRoot()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.TurnStarted(2, turnId: "turn-child", runId: "run-child", userMessageId: "user-msg-child", parentRunId: TestData.RootRunId));

        Assert.AreEqual(1, result.AppliedEventCount);
        Assert.AreEqual(1, result.IgnoredEventCount);
        Assert.AreEqual(2L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(TestData.RootTurnId, result.State.CurrentTurnId);
        Assert.AreEqual(TestData.RootRunId, result.State.CurrentRootRunId);
        Assert.HasCount(1, result.State.Turns);
    }

    // ============================================================ 终态与迟到事件

    [TestMethod]
    public void Apply_TurnCompleted_SetsTerminalState()
    {
        var state = TestData.Reduced(TestData.TurnStarted(1), TestData.TurnCompleted(2));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.AreEqual(TurnTerminalState.Completed, turn.TerminalState);
        Assert.IsFalse(turn.HasAssistantBody, "TurnCompleted 本身不得伪造助手正文");
        Assert.AreEqual(TurnTerminalState.Completed, state.CurrentRootTerminalState);
        Assert.AreEqual(2L, turn.LastSequence);
    }

    [TestMethod]
    public void Apply_TurnCompletedWithReply_SetsAssistantBodyAndIdentity()
    {
        var state = TestData.Reduced(
            TestData.TurnStarted(1),
            TestData.TurnCompleted(2, reply: "final answer", messageId: "assistant-msg-9"));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.AreEqual(TurnTerminalState.Completed, turn.TerminalState);
        Assert.IsTrue(turn.HasAssistantBody, "Completed terminal with reply proves an assistant body exists");
        Assert.AreEqual("assistant-msg-9", turn.AssistantMessageId);
        Assert.AreEqual(string.Empty, turn.ActivePreview, "terminal reply is not appended to the active preview");
    }

    [TestMethod]
    public void Apply_TurnFailedWithNoReply_StillHasNoAssistantBody()
    {
        var state = TestData.Reduced(
            TestData.TurnStarted(1),
            TestData.TurnFailed(2));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.AreEqual(TurnTerminalState.Failed, turn.TerminalState);
        Assert.IsFalse(turn.HasAssistantBody);
        Assert.IsNull(turn.AssistantMessageId);
    }

    [TestMethod]
    public void Apply_TurnFailed_ForcesNoAssistantBodyAndDoesNotFabricateAssistantMessageId()
    {
        var state = TestData.Reduced(
            TestData.TurnStarted(1),
            TestData.ContentAppended(2, "partial"),
            TestData.TurnFailed(3, messageId: "fake-assistant-message"));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.AreEqual(TurnTerminalState.Failed, turn.TerminalState);
        Assert.IsFalse(turn.HasAssistantBody);
        Assert.IsNull(turn.AssistantMessageId);
        Assert.AreEqual("partial", turn.ActivePreview);
        Assert.AreEqual(TurnTerminalState.Failed, state.CurrentRootTerminalState);
    }

    [TestMethod]
    public void Apply_TurnCancelled_ForcesNoAssistantBodyAndDoesNotFabricateAssistantMessageId()
    {
        var state = TestData.Reduced(
            TestData.TurnStarted(1),
            TestData.ContentAppended(2, "partial"),
            TestData.TurnCancelled(3));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.AreEqual(TurnTerminalState.Cancelled, turn.TerminalState);
        Assert.IsFalse(turn.HasAssistantBody);
        Assert.IsNull(turn.AssistantMessageId);
        Assert.AreEqual(TurnTerminalState.Cancelled, state.CurrentRootTerminalState);
    }

    [TestMethod]
    public void Apply_LateTurnStartedAfterTerminal_IsClassifiedAndDoesNotReopenTurn()
    {
        var first = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.MessageCompleted(2),
            TestData.TurnCompleted(3));

        var result = TestData.Apply(first.State, TestData.TurnStarted(4));

        Assert.AreEqual(1, result.LateAfterTerminalCount);
        Assert.AreEqual(0, result.AppliedEventCount);
        Assert.AreEqual(4L, result.State.ProjectedThroughSequence,
            "迟到事件必须消费掉游标，否则调用方会反复同一批");
        Assert.AreEqual(first.State.Revision, result.State.Revision,
            "迟到的 TurnStarted 不得改动摘要修订号");
        TestData.AssertTurnsEquivalent(first.State, result.State);
        Assert.AreEqual(TurnTerminalState.Completed, result.State.Turns[TestData.RootTurnId].TerminalState);
    }

    [TestMethod]
    public void Apply_LateContentAfterTerminal_DoesNotChangePreviewOrCounts()
    {
        var first = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.ContentAppended(2, "abc"),
            TestData.MessageCompleted(3),
            TestData.TurnCompleted(4));

        var result = TestData.Apply(first.State, TestData.ContentAppended(5, "XYZ"));

        Assert.AreEqual(1, result.LateAfterTerminalCount);
        Assert.AreEqual(0, result.AppliedEventCount);
        Assert.AreEqual(5L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(first.State.Revision, result.State.Revision);
        TestData.AssertTurnsEquivalent(first.State, result.State);

        var turn = result.State.Turns[TestData.RootTurnId];
        Assert.AreEqual("abc", turn.ActivePreview);
        Assert.AreEqual(1, turn.Process.ContentAppendCount);
    }

    [TestMethod]
    public void Apply_LateEventInsideSameBatch_IsClassifiedChronologically()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.MessageCompleted(2),
            TestData.TurnCompleted(3),
            TestData.ContentAppended(4, "late"));

        Assert.AreEqual(3, result.AppliedEventCount);
        Assert.AreEqual(1, result.LateAfterTerminalCount);
        Assert.AreEqual(4L, result.State.ProjectedThroughSequence);

        var turn = result.State.Turns[TestData.RootTurnId];
        Assert.AreEqual(string.Empty, turn.ActivePreview);
        Assert.AreEqual(0, turn.Process.ContentAppendCount);
        Assert.IsTrue(turn.HasAssistantBody);
    }

    [TestMethod]
    public void Apply_SecondTurnAfterTerminal_IsAccepted()
    {
        var first = TestData.Apply(TestData.TurnStarted(1), TestData.TurnCompleted(2));

        var result = TestData.Apply(
            first.State,
            TestData.TurnStarted(3, turnId: "turn-2", runId: "run-root-2", userMessageId: "user-msg-2"));

        Assert.AreEqual(1, result.AppliedEventCount);
        Assert.HasCount(2, result.State.Turns);
        Assert.AreEqual(TestData.RootTurnId, result.State.Turns[TestData.RootTurnId].TurnId);
        Assert.AreEqual(TurnTerminalState.Completed, result.State.Turns[TestData.RootTurnId].TerminalState);
        Assert.AreEqual("turn-2", result.State.CurrentTurnId);
        Assert.AreEqual("run-root-2", result.State.CurrentRootRunId);
        Assert.AreEqual(TurnTerminalState.Unknown, result.State.CurrentRootTerminalState);
    }

    // ============================================================ 正文与计数

    [TestMethod]
    public void Apply_MessageCompleted_SetsAssistantBodyAndIdentity()
    {
        var state = TestData.Reduced(
            TestData.TurnStarted(1),
            TestData.ContentAppended(2, "answer"),
            TestData.MessageCompleted(3, "assistant-msg-9"));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.IsTrue(turn.HasAssistantBody);
        Assert.AreEqual("assistant-msg-9", turn.AssistantMessageId);
        Assert.AreEqual(1, turn.Process.ContentAppendCount);
        Assert.AreEqual(TurnTerminalState.Unknown, turn.TerminalState);
    }

    [TestMethod]
    public void Apply_MessageCompletedWithoutMessageId_DoesNotFabricateIdentity()
    {
        var state = TestData.Reduced(
            TestData.TurnStarted(1),
            TestData.MessageCompleted(2, messageId: null));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.IsTrue(turn.HasAssistantBody);
        Assert.IsNull(turn.AssistantMessageId);
    }

    [TestMethod]
    public void Apply_ContentAndThinkingAppends_AreCounted()
    {
        var state = TestData.Reduced(
            TestData.TurnStarted(1),
            TestData.ContentAppended(2, "a"),
            TestData.ContentAppended(3, "b"),
            TestData.ThinkingAppended(4, "t1"),
            TestData.ThinkingAppended(5, "t2"));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.AreEqual(2, turn.Process.ContentAppendCount);
        Assert.AreEqual(2, turn.Process.ThinkingAppendCount);
        Assert.AreEqual("ab", turn.ActivePreview, "活动预览只拼接正文增量");
        Assert.AreEqual(5L, turn.LastSequence);
    }

    [TestMethod]
    public void Apply_ToolCallEvents_AreCounted()
    {
        var state = TestData.Reduced(
            TestData.TurnStarted(1),
            TestData.ToolRequested(2),
            TestData.ToolRequested(3),
            TestData.ToolCompleted(4),
            TestData.ToolFailed(5));

        var process = state.Turns[TestData.RootTurnId].Process;
        Assert.AreEqual(2, process.ToolCallRequestedCount);
        Assert.AreEqual(1, process.ToolCallCompletedCount);
        Assert.AreEqual(1, process.ToolCallFailedCount);
        Assert.AreEqual(1, state.Turns[TestData.RootTurnId].Process.Revision);
    }

    // ============================================================ 有界活动预览（UTF-8）

    [TestMethod]
    public void Apply_ActivePreview_KeepsBoundedUtf8Tail()
    {
        var huge = new string('文', 7_000); // 21000 UTF-8 bytes

        var state = TestData.Reduced(TestData.TurnStarted(1), TestData.ContentAppended(2, huge));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.IsTrue(turn.ActivePreviewTruncated);
        Assert.AreEqual(16_383, TestData.Utf8Bytes(turn.ActivePreview));
        Assert.AreEqual(new string('文', 5_461), turn.ActivePreview);
        TestData.AssertNoReplacementCharacters(turn.ActivePreview);

        var later = TestData.Apply(state, TestData.ContentAppended(3, "尾")).State;
        turn = later.Turns[TestData.RootTurnId];
        Assert.IsTrue(turn.ActivePreviewTruncated);
        Assert.AreEqual(16_383, TestData.Utf8Bytes(turn.ActivePreview));
        Assert.AreEqual(new string('文', 5_460) + "尾", turn.ActivePreview);
        TestData.AssertNoReplacementCharacters(turn.ActivePreview);
    }

    [TestMethod]
    public void Apply_ActivePreview_DoesNotSplitEmojiCodePoints()
    {
        var emojiRun = TestData.Repeat("😀", 5_000) + "x"; // 4*5000+1 = 20001 bytes

        var state = TestData.Reduced(TestData.TurnStarted(1), TestData.ContentAppended(2, emojiRun));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.IsTrue(turn.ActivePreviewTruncated);
        Assert.AreEqual(16_381, TestData.Utf8Bytes(turn.ActivePreview));
        Assert.AreEqual(TestData.Repeat("😀", 4_095) + "x", turn.ActivePreview);
        TestData.AssertNoReplacementCharacters(turn.ActivePreview);
    }

    [TestMethod]
    public void Apply_ActivePreview_ShortContentIsNotTruncated()
    {
        var state = TestData.Reduced(TestData.TurnStarted(1), TestData.ContentAppended(2, "你好"));

        var turn = state.Turns[TestData.RootTurnId];
        Assert.IsFalse(turn.ActivePreviewTruncated);
        Assert.AreEqual("你好", turn.ActivePreview);
        Assert.AreEqual(6, TestData.Utf8Bytes(turn.ActivePreview));
    }

    // ============================================================ 压缩状态

    [TestMethod]
    public void Apply_CompactionStartedThenCompleted_TracksRunningState()
    {
        var started = TestData.Apply(TestData.CompactionStarted(1, "compact-run-7")).State;

        Assert.IsNotNull(started.Compaction);
        Assert.IsTrue(started.Compaction!.IsRunning);
        Assert.AreEqual("compact-run-7", started.Compaction.RunId);
        Assert.AreEqual(1L, started.Compaction.StartedSequence);
        Assert.IsNull(started.Compaction.CompletedSequence);
        Assert.IsFalse(started.Compaction.LastAttemptFailed);
        Assert.AreEqual(1, started.Compaction.Revision);

        var completed = TestData.Apply(started, TestData.CompactionCompleted(2, "compact-run-7")).State;

        Assert.IsNotNull(completed.Compaction);
        Assert.IsFalse(completed.Compaction!.IsRunning);
        Assert.AreEqual(2L, completed.Compaction.CompletedSequence);
        Assert.IsFalse(completed.Compaction.LastAttemptFailed);
        Assert.AreEqual(2, completed.Compaction.Revision);
        Assert.AreEqual(2, completed.Revision);
    }

    [TestMethod]
    public void Apply_CompactionFailed_SetsLastAttemptFailed()
    {
        var started = TestData.Apply(TestData.CompactionStarted(1)).State;
        var failed = TestData.Apply(started, TestData.CompactionFailed(2)).State;

        Assert.IsNotNull(failed.Compaction);
        Assert.IsFalse(failed.Compaction!.IsRunning);
        Assert.IsTrue(failed.Compaction.LastAttemptFailed);
        Assert.IsNull(failed.Compaction.CompletedSequence);
    }

    [TestMethod]
    public void Apply_CompactionCompletedWithoutStarted_CreatesTerminalState()
    {
        var state = TestData.Reduced(TestData.CompactionCompleted(1, "compact-run-x"));

        Assert.IsNotNull(state.Compaction);
        Assert.IsFalse(state.Compaction!.IsRunning);
        Assert.AreEqual("compact-run-x", state.Compaction.RunId);
        Assert.AreEqual(0L, state.Compaction.StartedSequence);
        Assert.AreEqual(1L, state.Compaction.CompletedSequence);
        Assert.IsFalse(state.Compaction.LastAttemptFailed);
    }

    // ============================================================ 忽略/未知事件

    [TestMethod]
    public void Apply_UnknownKindOther_IsIgnoredWithoutChangingSummaries()
    {
        var first = TestData.Apply(TestData.TurnStarted(1), TestData.ContentAppended(2, "keep"));

        var result = TestData.Apply(
            first.State,
            TestData.Event(3, ConversationEventKind.Other, turnId: TestData.RootTurnId, textDelta: "ignored"));

        Assert.AreEqual(1, result.IgnoredEventCount);
        Assert.AreEqual(0, result.AppliedEventCount);
        Assert.AreEqual(3L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(first.State.Revision, result.State.Revision);
        TestData.AssertTurnsEquivalent(first.State, result.State);
    }

    [TestMethod]
    public void Apply_ContentEventWithoutTurn_IsIgnored()
    {
        var result = TestData.Apply(TestData.Event(1, ConversationEventKind.MessageContentAppended, textDelta: "x"));

        Assert.AreEqual(1, result.IgnoredEventCount);
        Assert.AreEqual(0, result.AppliedEventCount);
        Assert.AreEqual(1L, result.State.ProjectedThroughSequence);
        Assert.IsEmpty(result.State.Turns);
    }

    [TestMethod]
    public void Apply_TurnStartedWithoutTurnId_IsIgnored()
    {
        var result = TestData.Apply(TestData.Event(1, ConversationEventKind.TurnStarted, runId: "run-x"));

        Assert.AreEqual(1, result.IgnoredEventCount);
        Assert.AreEqual(0, result.AppliedEventCount);
        Assert.IsEmpty(result.State.Turns);
    }

    [TestMethod]
    public void Apply_ContentForUnknownTurn_CreatesPartialTurnSummary()
    {
        var result = TestData.Apply(TestData.ContentAppended(1, "orphan", turnId: "turn-9"));

        Assert.AreEqual(1, result.AppliedEventCount);
        Assert.HasCount(1, result.State.Turns);
        var turn = result.State.Turns["turn-9"];
        Assert.AreEqual("turn-9", turn.TurnId);
        Assert.IsNull(turn.RootRunId);
        Assert.AreEqual(1L, turn.FirstSequence);
        Assert.AreEqual(1L, turn.LastSequence);
        Assert.AreEqual("orphan", turn.ActivePreview);
        Assert.AreEqual(1, turn.Process.ContentAppendCount);
        Assert.AreEqual("turn-9", result.State.CurrentTurnId);
        Assert.IsNull(result.State.CurrentRootRunId);
    }

    // ============================================================ 游标/修订/版本推进

    [TestMethod]
    public void Apply_AdvancesProjectedThroughRevisionAndReadModelVersion()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.ContentAppended(2, "x"),
            TestData.ToolRequested(3));

        Assert.AreEqual(3L, result.State.ProjectedThroughSequence);
        Assert.AreEqual("evt-3", result.State.ProjectedThroughEventId);
        Assert.AreEqual(1, result.State.Revision);
        Assert.AreEqual(ConversationReadModelLimits.CurrentReadModelVersion, result.State.ReadModelVersion);
    }

    [TestMethod]
    public void Apply_MixedCategoryBatch_PartitionsCounts()
    {
        var first = TestData.Apply(TestData.TurnStarted(1));

        var result = TestData.Apply(
            first.State,
            TestData.ContentAppended(1, "duplicate"),                                     // 重复
            TestData.Event(2, ConversationEventKind.Other),                               // 忽略
            TestData.Event(9, ConversationEventKind.TurnStarted,                           // 跨会话
                conversationId: TestData.ForeignConversation, turnId: "foreign-turn", runId: "foreign-run"),
            TestData.ContentAppended(3, "new"));                                          // 应用

        Assert.AreEqual(1, result.DuplicateEventCount);
        Assert.AreEqual(1, result.IgnoredEventCount);
        Assert.AreEqual(1, result.ForeignConversationCount);
        Assert.AreEqual(1, result.AppliedEventCount);
        Assert.AreEqual(0, result.OutOfOrderEventCount);
        Assert.AreEqual(0, result.LateAfterTerminalCount);
        Assert.AreEqual(3L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(2, result.State.Revision);
        Assert.AreEqual("new", result.State.Turns[TestData.RootTurnId].ActivePreview);
    }
}
