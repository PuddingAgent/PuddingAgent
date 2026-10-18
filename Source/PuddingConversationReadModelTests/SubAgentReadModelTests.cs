namespace PuddingConversationReadModelTests;

/// <summary>
/// 子 run 隔离、子代理生命周期状态机与有界摘要（Lead 审查追加，Plan §3.2 / §4.1.2）：
/// <list type="bullet">
/// <item>子 run 的正文/思考/工具事件不得并入父 Turn 摘要，只消费游标并计入 Ignored；</item>
/// <item>子代理生命周期终态不可回退，冲突终态保留先到者（fail-closed）；</item>
/// <item><see cref="ConversationReadModelState.SubAgentRuns"/> 受
/// <see cref="ConversationReadModelLimits.SubAgentSummaryLimit"/> 限长，活动 run 优先保留，
/// 淘汰最旧终态 run，遗漏量计入 <see cref="ConversationReadModelState.SubAgentRunsOmitted"/>。</item>
/// </list>
/// </summary>
[TestClass]
public sealed class SubAgentReadModelTests
{
    // ============================================================ 子 run 内容隔离

    [TestMethod]
    public void Apply_ChildRunTurnScopedEvents_AreIgnoredAndDoNotPolluteParentTurnSummary()
    {
        var childTurnId = TestData.RootTurnId;
        var childRunId = "run-child-1";

        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.SubAgentCreated(2),
            TestData.Event(3, ConversationEventKind.MessageContentAppended, turnId: childTurnId, runId: childRunId, parentRunId: TestData.RootRunId, textDelta: "child-body"),
            TestData.Event(4, ConversationEventKind.MessageThinkingSummaryAppended, turnId: childTurnId, runId: childRunId, parentRunId: TestData.RootRunId, textDelta: "child-thinking"),
            TestData.Event(5, ConversationEventKind.ToolCallRequested, turnId: childTurnId, runId: childRunId, parentRunId: TestData.RootRunId),
            TestData.Event(6, ConversationEventKind.MessageCompleted, turnId: childTurnId, runId: childRunId, parentRunId: TestData.RootRunId, messageId: "child-msg"),
            TestData.Event(7, ConversationEventKind.ToolCallCompleted, turnId: childTurnId, runId: childRunId, parentRunId: TestData.RootRunId));

        // 只有 TurnStarted 与 SubAgentRunCreated 生效；5 条子 run 事件全部只消费游标。
        Assert.AreEqual(2, result.AppliedEventCount);
        Assert.AreEqual(5, result.IgnoredEventCount);
        Assert.AreEqual(0, result.LateAfterTerminalCount);
        Assert.AreEqual(0, result.OutOfOrderEventCount);
        Assert.AreEqual(7L, result.State.ProjectedThroughSequence);
        Assert.AreEqual("evt-7", result.State.ProjectedThroughEventId);

        var turn = result.State.Turns[TestData.RootTurnId];
        Assert.AreEqual(string.Empty, turn.ActivePreview, "子 run 正文不得并入父 Turn 活动预览");
        Assert.IsFalse(turn.ActivePreviewTruncated);
        Assert.AreEqual(0, turn.Process.ContentAppendCount);
        Assert.AreEqual(0, turn.Process.ThinkingAppendCount);
        Assert.AreEqual(0, turn.Process.ToolCallRequestedCount);
        Assert.AreEqual(0, turn.Process.ToolCallCompletedCount);
        Assert.AreEqual(0, turn.Process.ToolCallFailedCount);
        Assert.IsFalse(turn.HasAssistantBody, "子 run 的 MessageCompleted 不得伪造父 Turn 助手正文");
        Assert.IsNull(turn.AssistantMessageId);
        Assert.AreEqual(1, turn.Process.SubAgentRunCount);
        Assert.AreEqual(2L, turn.LastSequence, "子 run 内容事件不推进父 Turn 的 LastSequence");
        Assert.AreEqual(TurnTerminalState.Unknown, turn.TerminalState);

        Assert.HasCount(1, result.State.SubAgentRuns);
        Assert.AreEqual(childRunId, result.State.SubAgentRuns[0].RunId);
        Assert.AreEqual(SubAgentRunLifecycleState.Created, result.State.SubAgentRuns[0].State);
        Assert.AreEqual(0, result.State.SubAgentRunsOmitted);
    }

    [TestMethod]
    public void Apply_ChildRunTurnScopedEventAfterParentTerminal_IsLateAfterTerminal()
    {
        var first = TestData.Apply(TestData.TurnStarted(1), TestData.TurnCompleted(2));

        var result = TestData.Apply(
            first.State,
            TestData.Event(3, ConversationEventKind.MessageContentAppended, turnId: TestData.RootTurnId, runId: "run-child-1", parentRunId: TestData.RootRunId, textDelta: "late-child-body"));

        // 终态优先：即使带 ParentRunId，指向已终态 Turn 的事件也归 LateAfterTerminal。
        Assert.AreEqual(1, result.LateAfterTerminalCount);
        Assert.AreEqual(0, result.IgnoredEventCount);
        Assert.AreEqual(0, result.AppliedEventCount);
        Assert.AreEqual(3L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(first.State.Revision, result.State.Revision);
        TestData.AssertTurnsEquivalent(first.State, result.State);
    }

    // ============================================================ 有界子代理摘要

    [TestMethod]
    public void Apply_SubAgentRuns_AreBoundedToSummaryLimit_KeepingActiveAndEvictingOldestTerminal()
    {
        var events = new List<ConversationEventInput> { TestData.TurnStarted(1) };
        var sequence = 2L;

        events.Add(Created(sequence++, "run-active", TestData.RootRunId)); // 活动 run，必须始终保留
        for (var i = 1; i <= 19; i++)
        {
            var runId = $"run-t{i:D2}";
            events.Add(Created(sequence++, runId, TestData.RootRunId));
            events.Add(TestData.Event(sequence++, ConversationEventKind.SubAgentRunCompleted, turnId: TestData.RootTurnId, runId: runId, parentRunId: TestData.RootRunId));
        }

        events.Add(Created(sequence++, "run-t20", TestData.RootRunId)); // 触发淘汰最旧终态 run-t01
        events.Add(TestData.Event(sequence++, ConversationEventKind.SubAgentRunCompleted, turnId: TestData.RootTurnId, runId: "run-t20", parentRunId: TestData.RootRunId));
        events.Add(Created(sequence++, "run-t21", TestData.RootRunId)); // 再淘汰 run-t02

        var result = TestData.Apply(events.ToArray());

        Assert.AreEqual(43L, result.State.ProjectedThroughSequence);
        Assert.AreEqual(43, result.AppliedEventCount);
        Assert.AreEqual(0, result.IgnoredEventCount);

        var runs = result.State.SubAgentRuns;
        Assert.HasCount(20, runs, "SubAgentRuns 必须被 SubAgentSummaryLimit=20 限长");
        Assert.AreEqual(2, result.State.SubAgentRunsOmitted, "两次淘汰都计入 SubAgentRunsOmitted");

        Assert.IsFalse(RunsContain(runs, "run-t01"), "最旧终态 run-t01 应被淘汰");
        Assert.IsFalse(RunsContain(runs, "run-t02"), "次旧终态 run-t02 应被淘汰");
        Assert.IsTrue(RunsContain(runs, "run-active"), "活动 run 始终保留");
        Assert.IsTrue(RunsContain(runs, "run-t03"));
        Assert.IsTrue(RunsContain(runs, "run-t20"));
        Assert.IsTrue(RunsContain(runs, "run-t21"));
        Assert.AreEqual(SubAgentRunLifecycleState.Completed, FindRun(runs, "run-t20").State);
        Assert.AreEqual(SubAgentRunLifecycleState.Created, FindRun(runs, "run-t21").State);
        Assert.AreEqual(2, runs.Count(static run => run.IsActive), "仅 run-active 与新加入的 run-t21 为活动");

        // 父 Turn 的累计计数：21 个 run 进入过有界摘要 + 1 个初始活动 run = 22 次准入。
        Assert.AreEqual(22, result.State.Turns[TestData.RootTurnId].Process.SubAgentRunCount);
    }

    [TestMethod]
    public void Apply_SubAgentRuns_WhenAllActive_OmitsNewRunWithoutEviction()
    {
        var limits = ConversationReadModelLimits.Default with { SubAgentSummaryLimit = 3 };

        var result = ConversationReadModelReducer.Apply(TestData.EmptyState(), new[]
        {
            TestData.TurnStarted(1),
            Created(2, "run-a", TestData.RootRunId),
            Created(3, "run-b", TestData.RootRunId),
            Created(4, "run-c", TestData.RootRunId),
            Created(5, "run-d", TestData.RootRunId),
        }, limits);

        Assert.AreEqual(5L, result.State.ProjectedThroughSequence);
        Assert.HasCount(3, result.State.SubAgentRuns);
        Assert.AreEqual(1, result.State.SubAgentRunsOmitted, "全为活动 run 时新 run 不加入摘要，只计遗漏");
        Assert.IsFalse(RunsContain(result.State.SubAgentRuns, "run-d"));
        Assert.AreEqual(3, result.State.Turns[TestData.RootTurnId].Process.SubAgentRunCount);
        Assert.AreEqual(0, result.IgnoredEventCount, "遗漏进有界摘要的 run 事件仍消费游标，不按未知事件忽略");
    }

    [TestMethod]
    public void Apply_SubAgentRuns_EvictedRunReturning_DoesNotOverflowBoundedSummary()
    {
        var limits = ConversationReadModelLimits.Default with { SubAgentSummaryLimit = 3 };

        var events = new List<ConversationEventInput>
        {
            TestData.TurnStarted(1),
            Created(2, "run-r1", TestData.RootRunId),
            Completed(3, "run-r1"),
            Created(4, "run-r2", TestData.RootRunId),
            Completed(5, "run-r2"),
            Created(6, "run-r3", TestData.RootRunId),
            Completed(7, "run-r3"),
            Created(8, "run-r4", TestData.RootRunId), // 淘汰最旧终态 run-r1
            Started(9, "run-r1"),                    // 已淘汰的 run 再出现：按新 run 处理，必须不能撑爆
        };

        var result = ConversationReadModelReducer.Apply(TestData.EmptyState(), events.ToArray(), limits);

        Assert.HasCount(3, result.State.SubAgentRuns, "已淘汰 run 的后续事件不得把列表撑过上限");
        Assert.AreEqual(2, result.State.SubAgentRunsOmitted);
        Assert.IsFalse(RunsContain(result.State.SubAgentRuns, "run-r2"), "腾槽位时淘汰最旧终态 run-r2");
        Assert.IsTrue(RunsContain(result.State.SubAgentRuns, "run-r1"));
        Assert.AreEqual(SubAgentRunLifecycleState.Running, FindRun(result.State.SubAgentRuns, "run-r1").State);
        Assert.AreEqual(9L, FindRun(result.State.SubAgentRuns, "run-r1").LastSequence);
        Assert.AreEqual(3, result.State.SubAgentRuns.Select(static run => run.RunId).Distinct(StringComparer.Ordinal).Count());
    }

    // ============================================================ 生命周期状态机

    [TestMethod]
    public void Apply_SubAgentLifecycle_TerminalStateCannotRegress()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.SubAgentCreated(2),
            TestData.SubAgentLifecycle(3, ConversationEventKind.SubAgentRunCompleted),
            TestData.SubAgentLifecycle(4, ConversationEventKind.SubAgentRunStarted));

        Assert.AreEqual(3, result.AppliedEventCount);
        Assert.AreEqual(1, result.IgnoredEventCount, "终态之后的非终态事件只消费游标并计入 Ignored");
        Assert.AreEqual(4L, result.State.ProjectedThroughSequence);

        Assert.HasCount(1, result.State.SubAgentRuns);
        var run = result.State.SubAgentRuns[0];
        Assert.AreEqual(SubAgentRunLifecycleState.Completed, run.State);
        Assert.AreEqual(3L, run.LastSequence, "被忽略的回退事件不得改写 run 的 LastSequence");
        Assert.AreEqual(1, result.State.Turns[TestData.RootTurnId].Process.SubAgentRunCount, "同一 run 多次生命周期不重复计数");
    }

    [TestMethod]
    public void Apply_SubAgentLifecycle_ConflictingTerminalKeepsFirstTerminal()
    {
        var result = TestData.Apply(
            TestData.TurnStarted(1),
            TestData.SubAgentCreated(2),
            TestData.SubAgentLifecycle(3, ConversationEventKind.SubAgentRunCompleted),
            TestData.SubAgentLifecycle(4, ConversationEventKind.SubAgentRunFailed));

        Assert.AreEqual(3, result.AppliedEventCount);
        Assert.AreEqual(1, result.IgnoredEventCount, "冲突终态 fail-closed：保留先到终态");
        Assert.AreEqual(SubAgentRunLifecycleState.Completed, result.State.SubAgentRuns[0].State);
        Assert.AreEqual(3L, result.State.SubAgentRuns[0].LastSequence);
    }

    // ============================================================ 辅助

    private static ConversationEventInput Created(long sequence, string runId, string parentRunId)
        => TestData.Event(sequence, ConversationEventKind.SubAgentRunCreated, turnId: TestData.RootTurnId, runId: runId, parentRunId: parentRunId, subAgentId: "sub-agent-" + runId);

    private static ConversationEventInput Started(long sequence, string runId)
        => TestData.Event(sequence, ConversationEventKind.SubAgentRunStarted, turnId: TestData.RootTurnId, runId: runId, parentRunId: TestData.RootRunId);

    private static ConversationEventInput Completed(long sequence, string runId)
        => TestData.Event(sequence, ConversationEventKind.SubAgentRunCompleted, turnId: TestData.RootTurnId, runId: runId, parentRunId: TestData.RootRunId);

    private static bool RunsContain(IReadOnlyList<SubAgentRunSummary> runs, string runId)
        => runs.Any(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));

    private static SubAgentRunSummary FindRun(IReadOnlyList<SubAgentRunSummary> runs, string runId)
        => runs.First(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));
}
