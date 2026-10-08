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
    public void VerifyApplication_AppliesAndReportsReMeasuredAfter()
    {
        var store = new ContextUsageSnapshotStore();
        var history = BuildHistory(pairs: 40);
        var plan = CreatePlan(store, history);
        var summary = "## Current Work\n- continue implementation\n## Next Step\n- run tests";

        var verification = WarmPrefixCompaction.VerifyApplication(
            store, SessionId, history, tools: null, BuildConfig(), plan, summary);

        Assert.IsTrue(verification.CanApply, verification.Reason);
        Assert.AreEqual("applied", verification.Reason);
        Assert.IsNotNull(verification.AfterSnapshot);
        Assert.IsNotNull(verification.AppliedMessages);
        Assert.AreEqual(plan.RetainedMessages.Count + 1, verification.AppliedMessages!.Count);

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
            store, SessionId, mutated, tools: null, BuildConfig(), plan, "## Current Work\n- x");

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
            store, SessionId, history, tools: null, BuildConfig(), plan, oversizedSummary);

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
            store, SessionId, history, tools: null, BuildConfig(), tinyLimitPlan, "## Current Work\n- x");

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
}
