using PuddingContextPolicy;

namespace PuddingContextPolicyTests;

/// <summary>
/// 严格适用校验：任何相关变化都丢弃候选，尤其**新消息追加后旧候选必须失效**
/// （用户优先，旧摘要绝不覆盖新消息）。
/// </summary>
[TestClass]
public sealed class ContextCandidateApplicabilityTests
{
    private static FrozenCandidateDescriptor Frozen(
        string[] removed,
        string[] retained,
        long generation = 5,
        string revision = "rev-1",
        string route = "deepseek/flash",
        string tools = "tools-99",
        string policy = ContextPolicyVersion.Current) =>
        new("fingerprint-abc", generation, revision, route, tools, policy, removed, retained);

    private static CurrentContextDescriptor Current(
        string[] identities,
        long generation = 5,
        string revision = "rev-1",
        string route = "deepseek/flash",
        string tools = "tools-99",
        string policy = ContextPolicyVersion.Current) =>
        new(generation, revision, route, tools, policy, identities);

    [TestMethod]
    public void Validate_UnchangedContextIsApplicable()
    {
        var frozen = Frozen(["u-1", "a-1"], ["u-2", "a-2", "current-user"]);

        var result = ContextCandidateApplicability.Validate(
            frozen,
            Current(["sys-1", "u-1", "a-1", "u-2", "a-2", "current-user"]));

        Assert.AreEqual(CandidateApplicabilityVerdict.Applicable, result.Verdict);
        Assert.IsTrue(result.IsApplicable);
        Assert.AreEqual("applicable", result.Reason);
    }

    /// <summary>用户新消息到达（尾部之后追加）⇒ 旧候选必须失效。这是「用户优先」的核心断言。</summary>
    [TestMethod]
    public void Validate_NewlyAppendedMessageInvalidatesCandidate()
    {
        var frozen = Frozen(["u-1", "a-1"], ["u-2", "a-2", "current-user"]);

        var result = ContextCandidateApplicability.Validate(
            frozen,
            Current(["sys-1", "u-1", "a-1", "u-2", "a-2", "current-user", "next-user"]));

        Assert.AreEqual(CandidateApplicabilityVerdict.RetainedContextChanged, result.Verdict);
        Assert.IsFalse(result.IsApplicable);
        Assert.AreEqual("retained_context_changed", result.Reason);
    }

    [TestMethod]
    [DataRow("generation")]
    [DataRow("revision")]
    [DataRow("route")]
    [DataRow("tools")]
    [DataRow("policy")]
    public void Validate_AnyVersionChangeIsStale(string what)
    {
        var frozen = Frozen(["u-1", "a-1"], ["u-2", "current-user"]);
        var current = Current(
            ["sys-1", "u-1", "a-1", "u-2", "current-user"],
            generation: what == "generation" ? 6 : 5,
            revision: what == "revision" ? "rev-2" : "rev-1",
            route: what == "route" ? "deepseek/pro" : "deepseek/flash",
            tools: what == "tools" ? "tools-100" : "tools-99",
            policy: what == "policy" ? "context-policy/2" : ContextPolicyVersion.Current);

        var result = ContextCandidateApplicability.Validate(frozen, current);

        Assert.AreEqual(CandidateApplicabilityVerdict.StaleCandidate, result.Verdict);
        Assert.IsFalse(result.IsApplicable);
    }

    [TestMethod]
    public void Validate_RemovedOrReorderedSpanIsNotIntact()
    {
        var frozen = Frozen(["u-1", "a-1"], ["u-2", "current-user"]);

        // a-1 被删掉 ⇒ 跨度不再完整。
        var missing = ContextCandidateApplicability.Validate(
            frozen, Current(["sys-1", "u-1", "u-2", "current-user"]));
        Assert.AreEqual(CandidateApplicabilityVerdict.SpanNotIntact, missing.Verdict);

        // 顺序被换 ⇒ 跨度不再完整。
        var reordered = ContextCandidateApplicability.Validate(
            frozen, Current(["sys-1", "a-1", "u-1", "u-2", "current-user"]));
        Assert.AreEqual(CandidateApplicabilityVerdict.SpanNotIntact, reordered.Verdict);
    }

    /// <summary>
    /// 跨度完整、尾部也还在结尾，但**中间插入了一条消息**：保留后缀不再逐位一致 ⇒ 必须失效。
    /// （只比较「尾部是否结尾」会漏掉这一整类变化。）
    /// </summary>
    [TestMethod]
    public void Validate_InsertedMessageInsideRetainedSuffixInvalidates()
    {
        var frozen = Frozen(["u-1", "a-1"], ["u-2", "current-user"]);

        var result = ContextCandidateApplicability.Validate(
            frozen,
            Current(["sys-1", "u-1", "a-1", "injected", "u-2", "current-user"]));

        Assert.AreEqual(CandidateApplicabilityVerdict.RetainedContextChanged, result.Verdict);
        Assert.IsFalse(result.IsApplicable);
    }

    /// <summary>保留后缀被截短（尾部消息被删）同样失效。</summary>
    [TestMethod]
    public void Validate_ShrunkRetainedSuffixInvalidates()
    {
        var frozen = Frozen(["u-1", "a-1"], ["u-2", "a-2", "current-user"]);

        var result = ContextCandidateApplicability.Validate(
            frozen,
            Current(["sys-1", "u-1", "a-1", "u-2", "current-user"]));

        Assert.IsFalse(result.IsApplicable);
        Assert.AreEqual(CandidateApplicabilityVerdict.RetainedContextChanged, result.Verdict);
    }
}
