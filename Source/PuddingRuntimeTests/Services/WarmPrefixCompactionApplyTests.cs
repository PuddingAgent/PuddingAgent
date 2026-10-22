using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// F2 唯一应用边界：<see cref="WarmPrefixCompaction.VerifyApplication"/> 必须
/// ① 用**真实摘要**重新完整计量 after（不是计划估算）；② 对当前历史做严格 CAS（旧候选不得覆盖新消息）；
/// ③ 拒绝无收益与「加摘要后仍越界」的候选，且拒绝时历史保持原样。
/// </summary>
[TestClass]
public sealed class WarmPrefixCompactionApplyTests
{
    private const string SessionId = "session-warm-prefix-apply";

    private static LlmConfig BuildConfig() => new()
    {
        ModelId = "test-model",
        MaxContextTokens = 30_000,
        MaxOutputTokens = 1_000,
    };

    private static string Big => new('a', 4_000);

    private static List<ChatMessage> BuildHistory(int pairs)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "system prompt") };
        for (var i = 0; i < pairs; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"{Big} user-{i}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"{Big} assistant-{i}"));
        }

        return messages;
    }

    /// <summary>
    /// 「未变化」的当前值：模拟真实适配层在提交时从**当前** config / 当前工具清单 / 当前代次重算
    /// （不是从 plan 复制字段；plan 在这里只是提供预期相等的基准）。
    /// </summary>
    private static CurrentContextValues CurrentValuesFor(
        WarmPrefixCompactionPlan plan,
        IReadOnlyList<ChatMessage> history,
        LlmConfig? config = null,
        long? generation = null,
        string? routeVersion = null,
        string? toolSpecVersion = null)
        => new(
            Generation: generation ?? plan.SourceGeneration,
            RouteVersion: routeVersion ?? WarmPrefixCompaction.ResolveRouteVersion(config ?? BuildConfig()),
            ToolSpecVersion: toolSpecVersion ?? WarmPrefixCompaction.ResolveToolSpecVersion(history, tools: null));

    private static WarmPrefixCompactionPlan CreatePlan(
        ContextUsageSnapshotStore store,
        List<ChatMessage> history,
        double triggerRatio = 0.65,
        double targetRatio = 0.5)
    {
        Assert.IsTrue(WarmPrefixCompaction.TryCreatePlan(
            store,
            SessionId,
            history,
            history,
            tools: null,
            BuildConfig(),
            triggerRatio,
            targetRatio,
            out var plan));
        Assert.IsNotNull(plan);
        return plan!;
    }

    /// <summary>通过校验时必须给出**重新计量**的 after，而不是「未含摘要」的计划估算。</summary>
    [TestMethod]
    public void AppendOnlyRefresh_PreservesNewMessagesAndMeasuresOutboundEnvelope()
    {
        var store = new ContextUsageSnapshotStore();
        var frozen = BuildHistory(40);
        var plan = CreatePlan(store, frozen);
        var appended = new ChatMessage(ChatRole.User, "NEW USER CORRECTION");
        var current = frozen.Append(appended).ToArray();
        var outbound = current.ToArray();
        outbound[0] = new ChatMessage(ChatRole.System, new string('x', 12_000));
        var refreshed = WarmPrefixCompaction.RefreshAppendOnlyPlan(store, SessionId, frozen, current, outbound,
            null, BuildConfig(), plan, CurrentValuesFor(plan, current), out var reason);
        Assert.IsNotNull(refreshed, reason);
        var result = WarmPrefixCompaction.VerifyApplication(store, SessionId, current, null, BuildConfig(),
            refreshed, "Preserve current task and continue.", CurrentValuesFor(plan, current), outbound);
        Assert.IsTrue(result.CanApply, result.Reason);
        Assert.AreSame(appended, result.AppliedMessages![^1]);
        Assert.IsGreaterThan(LlmRequestTextBudget.Measure(result.AppliedMessages!, null), result.AfterTextBytes!.Value);
        var mutated = current.ToArray();
        mutated[0] = new ChatMessage(ChatRole.System, "CHANGED SYSTEM");
        Assert.IsNull(WarmPrefixCompaction.RefreshAppendOnlyPlan(store, SessionId, frozen, mutated, mutated,
            null, BuildConfig(), plan, CurrentValuesFor(plan, mutated), out _));
    }

    [TestMethod]
    public void VerifyApplication_AppliesAndReportsReMeasuredAfter()    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);
        var summary = "## Current Work\n- continue implementation\n## Next Step\n- run tests";

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, summary, CurrentValuesFor(plan, history));

        Assert.IsTrue(verification.CanApply, verification.Reason);
        Assert.AreEqual("applied", verification.Reason);
        Assert.IsNotNull(verification.AfterSnapshot);
        Assert.IsNotNull(verification.AppliedMessages);
        Assert.AreEqual(plan.RetainedMessages.Count + 1, verification.AppliedMessages!.Count);
        var checkpoint = verification.AppliedMessages[plan.ReplacementStart];
        Assert.IsTrue(checkpoint.IsContextSummary);
        Assert.AreEqual(ChatRole.Assistant, checkpoint.Role);
        Assert.AreNotEqual(WarmPrefixCompaction.ComputeMessageIdentity(checkpoint),
            WarmPrefixCompaction.ComputeMessageIdentity(checkpoint with { IsContextSummary = false }));

        // after 是重算值，且**大于**未含摘要的计划估算（摘要本身要占 token）。
        Assert.IsGreaterThan(
            plan.EstimatedRetainedSnapshot.UsedTokens,
            verification.AfterSnapshot!.UsedTokens);
        Assert.AreNotEqual(
            plan.EstimatedRetainedSnapshot.UsedTokens,
            verification.AfterSnapshot.UsedTokens);
        Assert.IsGreaterThan(0, verification.ReclaimTokens);
        // 计量是「只计量」的：验证过程不得改写 session 的准备快照。
        Assert.IsFalse(store.TryGet(SessionId, out _));
    }

    /// <summary>源历史变化 ⇒ 严格 CAS 拒绝，历史与内存都不动。</summary>
    [TestMethod]
    public void VerifyApplication_RejectsStaleCandidate()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        // 冻结之后历史发生变化（模拟新消息/新工具结果到达）。
        var mutated = history.Concat([new ChatMessage(ChatRole.User, "新的用户消息")]).ToArray();

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, mutated, tools: null, BuildConfig(), plan, "## Current Work\n- x", CurrentValuesFor(plan, mutated));

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual("history_revision_changed", verification.Reason);
        Assert.IsNull(verification.AppliedMessages, "拒绝时不得给出可替换的历史");
        Assert.IsNull(verification.AfterSnapshot, "未计量就没有 after，不得用计划估算顶替");
    }

    /// <summary>摘要不缩小 ⇒ 无收益拒绝。</summary>
    [TestMethod]
    public void VerifyApplication_RejectsNonShrinkingSummary()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        // 与被替换跨度同量级（甚至更大）的摘要。
        var oversizedSummary = new string('s', plan.RemovedTokenEstimate * 4);

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, oversizedSummary, CurrentValuesFor(plan, history));

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual(WarmPrefixCompactionReasons.SummaryNoGain, verification.Reason);
        Assert.IsNull(verification.AppliedMessages);
    }

    /// <summary>算式能过、但真实计量仍越界 ⇒ 一律拒绝（真实测量优先于计划算术）。</summary>
    [TestMethod]
    public void VerifyApplication_RejectsWhenMeasuredAfterStillExceedsHardLimit()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        // 人为把有效上限压到极小：此时摘要确实缩小了跨度，但整请求实测仍远超上限。
        var tinyLimitPlan = plan with { EffectiveInputLimit = 1_000 };

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), tinyLimitPlan, "## Current Work\n- x", CurrentValuesFor(tinyLimitPlan, history));

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual("after_summary_exceeds_hard_limit", verification.Reason);
        Assert.IsNotNull(verification.AfterSnapshot, "必须先真实计量过，才能判定越界");
        Assert.IsGreaterThan(1_000, verification.AfterSnapshot!.UsedTokens);
        Assert.IsNull(verification.AppliedMessages);
    }

    /// <summary>身份与 revision 必须稳定、且对内容敏感（CAS 判据的基础）。</summary>
    [TestMethod]
    public void MessageIdentityAndRevision_AreStableAndContentSensitive()
    {
        var first = new ChatMessage(ChatRole.User, "hello");
        var same = new ChatMessage(ChatRole.User, "hello");
        var different = new ChatMessage(ChatRole.User, "hello!");

        Assert.AreEqual(
            WarmPrefixCompaction.ComputeMessageIdentity(first),
            WarmPrefixCompaction.ComputeMessageIdentity(same));
        Assert.AreNotEqual(
            WarmPrefixCompaction.ComputeMessageIdentity(first),
            WarmPrefixCompaction.ComputeMessageIdentity(different));
        Assert.AreEqual(
            WarmPrefixCompaction.ComputeRevisionHash([first]),
            WarmPrefixCompaction.ComputeRevisionHash([same]));
        Assert.AreNotEqual(
            WarmPrefixCompaction.ComputeRevisionHash([first]),
            WarmPrefixCompaction.ComputeRevisionHash([first, different]));
    }

    /// <summary>
    /// F2 持久化：通过校验时必须带出**被替换跨度的原始对象**（供调用方解析持久 MessageId）。
    /// 用引用相等断言，确保切分准确 —— 切错会去标注错误的库行（= 静默隐藏真实消息）。
    /// </summary>
    [TestMethod]
    public void VerifyApplication_ExposesRemovedSpanObjects_ForDurableCommit()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);
        var summary = "## Current Work\n- continue implementation\n## Next Step\n- run tests";

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, summary, CurrentValuesFor(plan, history));

        Assert.IsTrue(verification.CanApply, verification.Reason);
        Assert.IsNotNull(verification.RemovedMessages);
        Assert.AreEqual(plan.RemovedMessageCount, verification.RemovedMessages!.Count);

        // 冻结布局：leading system 前缀 → removed span → retained suffix（见 TryCreatePlan）。
        var expected = history
            .Skip(plan.ReplacementStart)
            .Take(plan.RemovedMessageCount)
            .ToList();
        CollectionAssert.AreEqual(expected, verification.RemovedMessages!.ToList());
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.AreSame(expected[i], verification.RemovedMessages![i],
                "必须是跨度里的**同一个对象**，调用方才能按引用解析持久身份");
        }

        // 被替换跨度不得出现在提交后的历史里；前导 system 与保留后缀必须还在。
        foreach (var removed in verification.RemovedMessages!)
            Assert.IsFalse(verification.AppliedMessages!.Contains(removed), "被替换的消息不应留在提交后的历史中");
        Assert.AreSame(history[0], verification.AppliedMessages![0], "前导 system 必须原样保留");
        Assert.AreEqual(plan.RetainedMessages.Count + 1, verification.AppliedMessages!.Count);
    }

    /// <summary>被拒绝时不得带出跨度对象（调用方据此不落库）。</summary>
    [TestMethod]
    public void VerifyApplication_WhenRejected_ExposesNoRemovedSpan()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        // 空摘要 ⇒ summary_no_gain，历史原样。
        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, rawSummary: "   ", currentValues: CurrentValuesFor(plan, history));

        Assert.IsFalse(verification.CanApply);
        Assert.IsNull(verification.RemovedMessages);
        Assert.IsNull(verification.AppliedMessages);
    }

    // ── R4 门禁：CAS 必须用**提交时真实当前值**，逐项变更都要拒绝 ──────────────────

    /// <summary>
    /// 没有提供当前值 ⇒ 一律拒绝（`current_context_unavailable`）。
    /// 这条防的正是「拿 plan 的冻结值自比较」——那样 CAS 永远通过。
    /// </summary>
    [TestMethod]
    public void VerifyApplication_WithoutCurrentValues_IsRejected()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, "## Current Work\n- x");

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual(WarmPrefixCompactionReasons.CurrentContextUnavailable, verification.Reason);
        Assert.IsNull(verification.AppliedMessages);
        Assert.IsNull(verification.AfterSnapshot);
    }

    /// <summary>代次变化（例如期间有别的持久压缩提交）⇒ 拒绝，且不得给出可替换历史。</summary>
    [TestMethod]
    public void VerifyApplication_StaleGeneration_IsRejected()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, "## Current Work\n- x",
            CurrentValuesFor(plan, history, generation: plan.SourceGeneration + 1));

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual("generation_changed", verification.Reason);
        Assert.IsNull(verification.AppliedMessages);
        Assert.IsNull(verification.AfterSnapshot, "未通过 CAS 就不得计量 after");
    }

    /// <summary>路由变化（换 provider/model/protocol/endpoint 或容量）⇒ 拒绝。</summary>
    [TestMethod]
    public void VerifyApplication_ChangedRoute_IsRejected()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        var otherRoute = new LlmConfig
        {
            ModelId = "test-model",
            MaxContextTokens = 30_000,
            MaxOutputTokens = 1_000,
            Endpoint = "https://another-provider.example/v1",
            Protocol = "anthropic",
        };

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, "## Current Work\n- x",
            CurrentValuesFor(plan, history, config: otherRoute));

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual("route_changed", verification.Reason);
        Assert.IsNull(verification.AppliedMessages);
    }

    /// <summary>工具清单变化 ⇒ 拒绝（工具 schema 变了，旧候选的前缀不再成立）。</summary>
    [TestMethod]
    public void VerifyApplication_ChangedToolSpec_IsRejected()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, "## Current Work\n- x",
            CurrentValuesFor(plan, history, toolSpecVersion: "changed-tool-manifest"));

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual("tool_spec_changed", verification.Reason);
        Assert.IsNull(verification.AppliedMessages);
    }

    /// <summary>
    /// R4 §5.3：**工具调用参数**变化必须让身份变化（否则「同一工具换参数」会被判成未变而误用旧候选）。
    /// 这里构造含工具调用的历史，冻结候选后只改参数 JSON，当前 revision 必须不再匹配。
    /// </summary>
    [TestMethod]
    public void VerifyApplication_ToolArgumentsChange_IsRejected()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        history.Add(new ChatMessage(
            ChatRole.Assistant,
            "calling",
            ToolCalls: [new ToolCall("call-1", "lookup", "{\"query\":\"before\"}")]));
        history.Add(new ChatMessage(ChatRole.Tool, "result", ToolCallId: "call-1", ToolName: "lookup"));

        Assert.IsTrue(history.Select(WarmPrefixCompaction.ComputeMessageIdentity).Distinct().Count() == history.Count,
            "前提：历史里各条消息身份互不相同，便于观察参数变化");

        var plan = CreatePlan(store, history);

        // 只改工具参数（同 id、同 name、同内容长度不同）
        var mutated = history.ToList();
        var index = mutated.FindIndex(m => m.ToolCalls is { Count: > 0 });
        mutated[index] = mutated[index] with
        {
            ToolCalls = [new ToolCall("call-1", "lookup", "{\"query\":\"after\"}")],
        };

        var beforeIdentity = WarmPrefixCompaction.ComputeMessageIdentity(history[index]);
        var afterIdentity = WarmPrefixCompaction.ComputeMessageIdentity(mutated[index]);
        Assert.AreNotEqual(beforeIdentity, afterIdentity, "参数变化必须改变消息身份");

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, mutated, tools: null, BuildConfig(), plan, "## Current Work\n- x",
            CurrentValuesFor(plan, mutated));

        Assert.IsFalse(verification.CanApply);
        Assert.AreEqual("history_revision_changed", verification.Reason);
        Assert.IsNull(verification.AppliedMessages);
    }

    /// <summary>对照：什么都不变 ⇒ 仍然成功（证明上面的拒绝不是"一律拒绝"）。</summary>
    [TestMethod]
    public void VerifyApplication_UnchangedCurrentValues_StillApplies()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, "## Current Work\n- x",
            CurrentValuesFor(plan, history));

        Assert.IsTrue(verification.CanApply, verification.Reason);
        Assert.AreEqual("applied", verification.Reason);
    }

    [TestMethod]
    public void ContinuationOnlyMutation_InvalidatesFrozenRevision()
    {
        var before = new ChatMessage(ChatRole.Assistant, null,
            ContinuationState: new("responses", ["{\"type\":\"reasoning\",\"text\":\"before\"}"]));
        var after = before with { ContinuationState = new("responses", ["{\"type\":\"reasoning\",\"text\":\"after\"}"]) };
        Assert.AreNotEqual(WarmPrefixCompaction.ComputeMessageIdentity(before), WarmPrefixCompaction.ComputeMessageIdentity(after));
        var parts = BoundedCompactionSummary.CreateParts([before]);
        StringAssert.Contains(string.Join("\n", parts.SelectMany(p => p).Select(m => m.Content)), "before");
    }

    [TestMethod]
    public void RejectedActualTextMeasurement_RemainsAvailableWithoutPublishingOrMutatingHistory()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(40);
        var original = history.ToArray();
        var plan = CreatePlan(store, history);
        const string summary = "## Next Step\n- continue the task";
        var expected = LlmRequestTextBudget.Measure(WarmPrefixCompaction.TryCreateCheckpoint(plan, summary)!, null);
        var rejected = WarmPrefixCompaction.VerifyApplication(store, SessionId, history, null, BuildConfig(),
            plan with { MaxRequestTextBytes = (int)expected - 1 }, summary, CurrentValuesFor(plan, history));
        Assert.IsFalse(rejected.CanApply);
        Assert.AreEqual("after_summary_exceeds_text_budget", rejected.Reason);
        Assert.AreEqual(expected, rejected.AfterTextBytes);
        Assert.IsNotNull(rejected.AfterSnapshot);
        Assert.IsNull(rejected.AppliedMessages);
        CollectionAssert.AreEqual(original, history);
        Assert.IsFalse(store.TryGet(SessionId, out _));
    }
}
