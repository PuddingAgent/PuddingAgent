namespace PuddingConversationReadModelTests;

/// <summary>
/// <see cref="ProcessSummary"/> 完整性语义（Lead 审查追加，Plan §3.2）：
/// 活动 Turn 为 partial/turn_active；缺 TurnStarted 头为 partial/turn_header_missing；
/// 初始化未 Ready 为 partial/baseline_incomplete；只有 Ready 且观察到 TurnStarted 头的终态 Turn
/// 才是 complete（完整性只描述过程计数覆盖，不代表有助手正文）。
/// </summary>
[TestClass]
public sealed class ProcessCompletenessTests
{
    [TestMethod]
    public void ActiveAndTerminalReadyTurn_WithTurnHeader_ReportsActiveThenComplete()
    {
        var active = TestData.Apply(TestData.TurnStarted(1)).State;

        var activeTurn = active.Turns[TestData.RootTurnId];
        Assert.AreEqual(SummaryCompleteness.Partial, activeTurn.Process.Completeness);
        Assert.AreEqual("turn_active", activeTurn.Process.IncompleteReason);
        Assert.AreEqual(1, activeTurn.Process.Revision);

        var terminal = TestData.Apply(active, TestData.TurnCompleted(2)).State;

        var terminalTurn = terminal.Turns[TestData.RootTurnId];
        Assert.AreEqual(SummaryCompleteness.Complete, terminalTurn.Process.Completeness);
        Assert.IsNull(terminalTurn.Process.IncompleteReason);
        Assert.AreEqual(2, terminalTurn.Process.Revision, "终态事件必须递增该 Turn 的 Process.Revision");
        Assert.AreEqual(2, terminal.Revision);
    }

    [TestMethod]
    public void ReadyTurnWithoutTurnHeader_TerminalStaysPartialWithHeaderMissing()
    {
        var active = TestData.Apply(TestData.ContentAppended(1, "orphan", turnId: "turn-9")).State;

        var activeTurn = active.Turns["turn-9"];
        Assert.AreEqual(SummaryCompleteness.Partial, activeTurn.Process.Completeness);
        Assert.AreEqual("turn_header_missing", activeTurn.Process.IncompleteReason);

        var terminal = TestData.Apply(active, TestData.TurnCompleted(2, turnId: "turn-9")).State;

        var terminalTurn = terminal.Turns["turn-9"];
        Assert.AreEqual(SummaryCompleteness.Partial, terminalTurn.Process.Completeness);
        Assert.AreEqual("turn_header_missing", terminalTurn.Process.IncompleteReason);
        Assert.AreEqual(2, terminalTurn.Process.Revision);
    }

    [TestMethod]
    public void TurnStartedAfterContentFirstCreation_UpgradesReasonToTurnActive()
    {
        var contentFirst = TestData.Apply(TestData.ContentAppended(1, "orphan", turnId: "turn-9")).State;

        var withHeader = TestData.Apply(contentFirst, TestData.TurnStarted(2, turnId: "turn-9")).State;

        var turn = withHeader.Turns["turn-9"];
        Assert.AreEqual(SummaryCompleteness.Partial, turn.Process.Completeness);
        Assert.AreEqual("turn_active", turn.Process.IncompleteReason, "观察到 TurnStarted 头后不再宣称 turn_header_missing");
    }

    [TestMethod]
    public void NonReadyInitialization_StaysPartialWithBaselineIncomplete()
    {
        foreach (var initialization in new[]
                 {
                     ReadModelInitializationState.Uninitialized,
                     ReadModelInitializationState.BaselineBuilding,
                     ReadModelInitializationState.Partial,
                 })
        {
            var state = ConversationReadModelReducer.CreateEmpty(TestData.Conversation, initialization);

            var active = TestData.Apply(state, TestData.TurnStarted(1)).State;
            Assert.AreEqual(SummaryCompleteness.Partial, active.Turns[TestData.RootTurnId].Process.Completeness);
            Assert.AreEqual("baseline_incomplete", active.Turns[TestData.RootTurnId].Process.IncompleteReason);

            var terminal = TestData.Apply(active, TestData.TurnCompleted(2)).State;
            Assert.AreEqual(SummaryCompleteness.Partial, terminal.Turns[TestData.RootTurnId].Process.Completeness);
            Assert.AreEqual("baseline_incomplete", terminal.Turns[TestData.RootTurnId].Process.IncompleteReason);
        }
    }

    [TestMethod]
    public void FailedAndCancelledTurns_StillDescribeCompleteCoverageWithoutAssistantBody()
    {
        var failed = TestData.Apply(TestData.TurnStarted(1), TestData.TurnFailed(2)).State;
        var failedTurn = failed.Turns[TestData.RootTurnId];

        Assert.AreEqual(SummaryCompleteness.Complete, failedTurn.Process.Completeness,
            "完整性只描述过程计数覆盖，失败 Turn 的计数同样可以完整");
        Assert.IsNull(failedTurn.Process.IncompleteReason);
        Assert.AreEqual(TurnTerminalState.Failed, failedTurn.TerminalState);
        Assert.IsFalse(failedTurn.HasAssistantBody);
        Assert.AreEqual(1, failedTurn.Process.Revision);

        var cancelled = TestData.Apply(TestData.TurnStarted(1), TestData.TurnCancelled(2)).State;
        var cancelledTurn = cancelled.Turns[TestData.RootTurnId];

        Assert.AreEqual(SummaryCompleteness.Complete, cancelledTurn.Process.Completeness);
        Assert.IsNull(cancelledTurn.Process.IncompleteReason);
        Assert.AreEqual(TurnTerminalState.Cancelled, cancelledTurn.TerminalState);
        Assert.IsFalse(cancelledTurn.HasAssistantBody);
    }

    // ============================================================ 子代理摘要截断 ⇒ 完整性降级

    [TestMethod]
    public void TerminalTurnWithOmittedSubAgentRuns_DowngradesCompleteToSubAgentSummaryTruncated()
    {
        var events = BuildTurnWithFullSubAgentSummary(thenComplete: true);

        var state = TestData.Apply(events.ToArray()).State;
        var turn = state.Turns[TestData.RootTurnId];

        Assert.AreEqual(1, state.SubAgentRunsOmitted);
        Assert.HasCount(20, state.SubAgentRuns, "有界摘要仍保留上限内 20 行");
        Assert.AreEqual(21, turn.Process.SubAgentRunCount, "SubAgentRunCount = 进入有界摘要的 run 行数");
        Assert.AreEqual(TurnTerminalState.Completed, turn.TerminalState);
        Assert.AreEqual(SummaryCompleteness.Partial, turn.Process.Completeness,
            "omitted>0 时计数不完整，终态也不得宣称 Complete");
        Assert.AreEqual("subagent_summary_truncated", turn.Process.IncompleteReason);
    }

    [TestMethod]
    public void ActiveTurnWithOmittedSubAgentRuns_ReportsSubAgentSummaryTruncated()
    {
        var events = BuildTurnWithFullSubAgentSummary(thenComplete: false);

        var state = TestData.Apply(events.ToArray()).State;
        var turn = state.Turns[TestData.RootTurnId];

        Assert.AreEqual(TurnTerminalState.Unknown, turn.TerminalState);
        Assert.AreEqual(SummaryCompleteness.Partial, turn.Process.Completeness);
        Assert.AreEqual("subagent_summary_truncated", turn.Process.IncompleteReason,
            "subagent_summary_truncated 优先于 turn_active");
        Assert.AreEqual(1, state.SubAgentRunsOmitted);
    }

    [TestMethod]
    public void NonReadyInitialization_PriorityOverSubAgentSummaryTruncated()
    {
        var state = ConversationReadModelReducer.CreateEmpty(TestData.Conversation, ReadModelInitializationState.BaselineBuilding);
        var active = ConversationReadModelReducer.Apply(state, BuildTurnWithFullSubAgentSummary(thenComplete: false).ToArray()).State;

        Assert.AreEqual("baseline_incomplete", active.Turns[TestData.RootTurnId].Process.IncompleteReason,
            "baseline_incomplete 优先级最高，不得被 subagent_summary_truncated 覆盖");

        var completed = BuildTurnWithFullSubAgentSummary(thenComplete: true);
        var terminal = ConversationReadModelReducer.Apply(state, completed.ToArray()).State;
        Assert.AreEqual(SummaryCompleteness.Partial, terminal.Turns[TestData.RootTurnId].Process.Completeness);
        Assert.AreEqual("baseline_incomplete", terminal.Turns[TestData.RootTurnId].Process.IncompleteReason);
    }

    [TestMethod]
    public void MissingTurnHeader_PriorityOverSubAgentSummaryTruncated_AndHeaderArrivalRevealsTruncation()
    {
        var contentFirst = TestData.Apply(TestData.ContentAppended(1, "orphan", turnId: "turn-9")).State;

        var runEvents = BuildFullSubAgentSummary(TestData.RootTurnId, "turn-9", startSequence: 2);
        var afterRuns = TestData.Apply(contentFirst, runEvents.ToArray());

        var turn = afterRuns.State.Turns["turn-9"];
        Assert.AreEqual(1, afterRuns.State.SubAgentRunsOmitted);
        Assert.AreEqual("turn_header_missing", turn.Process.IncompleteReason,
            "turn_header_missing 优先于 subagent_summary_truncated");

        var headerSequence = runEvents.Max(static ev => ev.Sequence) + 1;
        var afterHeader = TestData.Apply(afterRuns.State, TestData.TurnStarted(headerSequence, turnId: "turn-9")).State;

        Assert.AreEqual("subagent_summary_truncated", afterHeader.Turns["turn-9"].Process.IncompleteReason,
            "补到 TurnStarted 头之后，已知的 omitted 必须体现为 subagent_summary_truncated 而不是 turn_active");
    }

    [TestMethod]
    public void TerminalTurnWithSubAgentRunsButNoOmission_StaysComplete()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.SubAgentCreated(2, turnId: TestData.RootTurnId),
            TestData.SubAgentLifecycle(3, ConversationEventKind.SubAgentRunCompleted),
            TestData.TurnCompleted(4));

        var turn = result.State.Turns[TestData.RootTurnId];
        Assert.AreEqual(0, result.State.SubAgentRunsOmitted);
        Assert.AreEqual(1, turn.Process.SubAgentRunCount);
        Assert.AreEqual(TurnTerminalState.Completed, turn.TerminalState);
        Assert.AreEqual(SummaryCompleteness.Complete, turn.Process.Completeness);
        Assert.IsNull(turn.Process.IncompleteReason);
    }

    // ============================================================ 辅助

    private static List<ConversationEventInput> BuildTurnWithFullSubAgentSummary(bool thenComplete)
    {
        var events = new List<ConversationEventInput> { TestData.TurnStarted(1) };
        events.AddRange(BuildFullSubAgentSummary(TestData.RootTurnId, TestData.RootTurnId, startSequence: 2, out var nextSequence));
        if (thenComplete)
            events.Add(TestData.TurnCompleted(nextSequence));
        return events;
    }

    private static List<ConversationEventInput> BuildFullSubAgentSummary(
        string turnId,
        string ownerTurnId,
        long startSequence)
        => BuildFullSubAgentSummary(turnId, ownerTurnId, startSequence, out _);

    private static List<ConversationEventInput> BuildFullSubAgentSummary(
        string turnId,
        string ownerTurnId,
        long startSequence,
        out long nextSequence)
    {
        var events = new List<ConversationEventInput>();
        var sequence = startSequence;
        for (var i = 1; i <= ConversationReadModelLimits.Default.SubAgentSummaryLimit; i++)
        {
            var runId = $"{turnId}-run-{i:D2}";
            events.Add(RunEvent(sequence++, ConversationEventKind.SubAgentRunCreated, ownerTurnId, runId));
            events.Add(RunEvent(sequence++, ConversationEventKind.SubAgentRunCompleted, ownerTurnId, runId));
        }

        // 第 21 个 run：触发淘汰最旧终态 run ⇒ SubAgentRunsOmitted=1。
        events.Add(RunEvent(sequence++, ConversationEventKind.SubAgentRunCreated, ownerTurnId, $"{turnId}-run-overflow"));
        nextSequence = sequence;
        return events;
    }

    private static ConversationEventInput RunEvent(long sequence, ConversationEventKind kind, string turnId, string runId)
        => TestData.Event(sequence, kind, turnId: turnId, runId: runId, parentRunId: TestData.RootRunId, subAgentId: "sub-agent-" + runId);
}
