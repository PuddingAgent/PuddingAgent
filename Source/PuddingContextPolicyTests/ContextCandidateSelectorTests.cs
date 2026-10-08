using PuddingContextPolicy;

namespace PuddingContextPolicyTests;

/// <summary>
/// 候选边界与指纹：只移除完整旧会话单元，绝不动前导 System、受保护尾部与当前用户轮；
/// 指纹只由身份与版本构成（可复现、与正文无关）。
/// </summary>
[TestClass]
public sealed class ContextCandidateSelectorTests
{
    private static readonly ContextCapacityResult SmallCapacity =
        ContextCapacityArithmetic.Resolve(new ContextCapacityInputs(ModelWindowTokens: 1_000_000));

    private static ContextMessageShape Msg(string id, ContextMessageRole role, int tokens) => new(id, role, tokens);

    /// <summary>前导 system + 若干「user/assistant」旧轮 + 当前用户轮。</summary>
    private static List<ContextMessageShape> BuildHistory(int oldUnits, int tokensPerMessage = 1_000)
    {
        var messages = new List<ContextMessageShape> { Msg("sys-1", ContextMessageRole.System, 5_000) };
        for (var i = 0; i < oldUnits; i++)
        {
            messages.Add(Msg($"u-{i}", ContextMessageRole.User, tokensPerMessage));
            messages.Add(Msg($"a-{i}", ContextMessageRole.Assistant, tokensPerMessage));
        }

        messages.Add(Msg("current-user", ContextMessageRole.User, tokensPerMessage));
        return messages;
    }

    private static ContextCapacityResult CapacityWithEffectiveLimit(int effectiveInputTokens) =>
        ContextCapacityArithmetic.Resolve(new ContextCapacityInputs(ModelWindowTokens: effectiveInputTokens));

    [TestMethod]
    public void Select_BelowSoftTrigger_CreatesNothing()
    {
        var result = ContextCandidateSelector.Select(
            BuildHistory(oldUnits: 2),
            nonMessageTokens: 0,
            CapacityWithEffectiveLimit(1_000_000),
            new CandidateSelectionOptions(TriggerRatio: 0.80, TargetRatio: 0.50),
            routeVersion: "deepseek/flash",
            toolSpecVersion: "tools-1",
            sourceGeneration: 7,
            sourceRevision: "rev-1");

        Assert.IsFalse(result.Created);
        Assert.AreEqual("below_soft_threshold", result.Reason);
        Assert.IsNull(result.Fingerprint);
    }

    [TestMethod]
    public void Select_RemovesWholeOldUnits_AndKeepsSystemProtectedTailAndCurrentUser()
    {
        var history = BuildHistory(oldUnits: 40);
        var expectedTail = history.TakeLast(8).Select(m => m.Identity).ToArray();

        var result = ContextCandidateSelector.Select(
            history,
            nonMessageTokens: 67_533,
            CapacityWithEffectiveLimit(150_000),
            new CandidateSelectionOptions(ProtectedTailMessages: 8, TriggerRatio: 0.80, TargetRatio: 0.50),
            routeVersion: "deepseek/flash",
            toolSpecVersion: "tools-99",
            sourceGeneration: 12,
            sourceRevision: "rev-42");

        Assert.IsTrue(result.Created, result.Reason);
        Assert.IsGreaterThan(0, result.RemovedCount);
        Assert.IsLessThan(result.InitialUsedTokens, result.ProjectedUsedTokensExcludingSummary);

        // 前导 system 不动、被替换跨度从 system 前缀之后开始。
        Assert.AreEqual(1, result.RemovedFromIndex);
        Assert.IsFalse(result.RemovedMessages.Any(m => m.Role == ContextMessageRole.System));
        // 保留后缀的末尾就是受保护尾部（最后 8 条），且当前用户轮保留在被替换跨度之外。
        Assert.IsGreaterThanOrEqualTo(8, result.RetainedMessages.Count);
        CollectionAssert.AreEqual(
            expectedTail,
            result.RetainedMessages.TakeLast(8).Select(m => m.Identity).ToArray());
        Assert.IsTrue(result.RetainedMessages.Any(m => m.Identity == "current-user"));
        Assert.IsFalse(result.RemovedMessages.Any(m => m.Identity == "current-user"));
        Assert.AreEqual("candidate_created", result.Reason);
    }

    /// <summary>激进目标下也不得移除当前用户轮（当前 Turn 围栏优先）。</summary>
    [TestMethod]
    public void Select_NeverRemovesCurrentUserTurn_EvenWithAggressiveTarget()
    {
        var history = BuildHistory(oldUnits: 40);

        var result = ContextCandidateSelector.Select(
            history,
            nonMessageTokens: 0,
            CapacityWithEffectiveLimit(60_000),
            new CandidateSelectionOptions(ProtectedTailMessages: 8, TriggerRatio: 0.10, TargetRatio: 0.05),
            routeVersion: "r",
            toolSpecVersion: "t",
            sourceGeneration: 1,
            sourceRevision: "rev");

        Assert.IsTrue(result.Created, result.Reason);
        Assert.IsFalse(result.RemovedMessages.Any(m => m.Identity == "current-user"));
        Assert.IsTrue(result.RetainedMessages.Any(m => m.Identity == "current-user"));
    }

    /// <summary>只按完整会话单元移除：不会把 assistant 与它后面的 tool 结果拆开。</summary>
    [TestMethod]
    public void Select_NeverSplitsToolExchangeFromItsCall()
    {
        var history = new List<ContextMessageShape>
        {
            Msg("sys-1", ContextMessageRole.System, 1_000),
            Msg("u-1", ContextMessageRole.User, 1_000),
            Msg("a-1", ContextMessageRole.Assistant, 1_000),
            Msg("t-1", ContextMessageRole.Tool, 1_000),
            Msg("t-2", ContextMessageRole.Tool, 1_000),
            Msg("u-2", ContextMessageRole.User, 1_000),
            Msg("a-2", ContextMessageRole.Assistant, 1_000),
            // 尾部 8 条之内 → 受保护
            Msg("u-3", ContextMessageRole.User, 1_000),
            Msg("a-3", ContextMessageRole.Assistant, 1_000),
            Msg("u-4", ContextMessageRole.User, 1_000),
            Msg("a-4", ContextMessageRole.Assistant, 1_000),
            Msg("u-5", ContextMessageRole.User, 1_000),
            Msg("a-5", ContextMessageRole.Assistant, 1_000),
            Msg("u-6", ContextMessageRole.User, 1_000),
            Msg("a-6", ContextMessageRole.Assistant, 1_000),
            Msg("current-user", ContextMessageRole.User, 1_000),
        };

        var result = ContextCandidateSelector.Select(
            history,
            nonMessageTokens: 0,
            CapacityWithEffectiveLimit(6_000),
            new CandidateSelectionOptions(ProtectedTailMessages: 8, TriggerRatio: 0.10, TargetRatio: 0.40),
            routeVersion: "r",
            toolSpecVersion: "t",
            sourceGeneration: 1,
            sourceRevision: "rev");

        Assert.IsTrue(result.Created, result.Reason);
        // 被移除的第一个单元必须是 u-1+（含其 tool 结果）；不能只删 a-1 留着 t-1/t-2。
        var removedIds = result.RemovedMessages.Select(m => m.Identity).ToArray();
        if (removedIds.Contains("a-1"))
        {
            Assert.IsTrue(removedIds.Contains("t-1") && removedIds.Contains("t-2"),
                "tool 结果必须与其 assistant 调用一起移除");
        }

        // 无论删到哪里，都不能把 tool 结果留给一个已被删除的调用。
        var retainedIds = history.Select(m => m.Identity)
            .Except(removedIds, StringComparer.Ordinal)
            .ToArray();
        if (!retainedIds.Contains("a-1"))
            Assert.IsFalse(retainedIds.Contains("t-1") || retainedIds.Contains("t-2"));
    }

    [TestMethod]
    public void Fingerprint_IsDeterministic_AndDependsOnlyOnIdentityAndVersions()
    {
        var history = BuildHistory(oldUnits: 40);
        var capacity = CapacityWithEffectiveLimit(150_000);
        var options = new CandidateSelectionOptions(ProtectedTailMessages: 8, TriggerRatio: 0.80, TargetRatio: 0.50);

        var first = ContextCandidateSelector.Select(
            history, 67_533, capacity, options, "deepseek/flash", "tools-99", 12, "rev-42");
        var second = ContextCandidateSelector.Select(
            history, 67_533, capacity, options, "deepseek/flash", "tools-99", 12, "rev-42");

        Assert.IsTrue(first.Created, first.Reason);
        Assert.IsNotNull(first.Fingerprint);
        Assert.AreEqual(first.Fingerprint, second.Fingerprint, "同输入必须得到同指纹");

        // 代次/路由/工具版本任一变化都必须改变指纹（旧候选因此不可复用）。
        var otherGeneration = ContextCandidateSelector.Select(
            history, 67_533, capacity, options, "deepseek/flash", "tools-99", 13, "rev-42");
        var otherRoute = ContextCandidateSelector.Select(
            history, 67_533, capacity, options, "deepseek/pro", "tools-99", 12, "rev-42");
        var otherTools = ContextCandidateSelector.Select(
            history, 67_533, capacity, options, "deepseek/flash", "tools-100", 12, "rev-42");

        Assert.AreNotEqual(first.Fingerprint, otherGeneration.Fingerprint);
        Assert.AreNotEqual(first.Fingerprint, otherRoute.Fingerprint);
        Assert.AreNotEqual(first.Fingerprint, otherTools.Fingerprint);

        // 指纹与正文/估算无关：只由身份与版本构成（token 估算变化不改变指纹）。
        var removedSpan = new[] { Msg("u-1", ContextMessageRole.User, 10) };
        var retainedSuffix = new[] { Msg("u-2", ContextMessageRole.User, 20) };
        var baseline = ContextCandidateSelector.ComputeFingerprint(
            12, "rev-42", "deepseek/flash", "tools-99", removedSpan, retainedSuffix);
        var reEstimated = ContextCandidateSelector.ComputeFingerprint(
            12, "rev-42", "deepseek/flash", "tools-99",
            [Msg("u-1", ContextMessageRole.User, 9_999)],
            [Msg("u-2", ContextMessageRole.User, 9_999)]);

        Assert.AreEqual(baseline, reEstimated);
    }
}
