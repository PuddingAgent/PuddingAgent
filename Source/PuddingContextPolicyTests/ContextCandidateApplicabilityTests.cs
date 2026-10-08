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

    /// <summary>
    /// 契约回归：按文档用 <c>ContextCandidateSelector</c> 的结果构造描述符
    /// （<c>RemovedMessages</c> 身份 + <c>RetainedSuffixIdentities</c>）必须判定为 Applicable。
    /// 若误用 <c>RetainedMessages</c> 的全量身份（含跨度之前的前导 system），
    /// 与 Validate 的 afterSpan 永远不等，会稳定退化成 retained_context_changed。
    /// </summary>
    [TestMethod]
    public void Validate_RoundTripFromSelection_IsApplicable()
    {
        var history = new List<ContextMessageShape> { new("sys-1", ContextMessageRole.System, 5_000) };
        for (var i = 0; i < 40; i++)
        {
            history.Add(new ContextMessageShape($"u-{i}", ContextMessageRole.User, 8_000));
            history.Add(new ContextMessageShape($"a-{i}", ContextMessageRole.Assistant, 8_000));
        }

        var capacity = ContextCapacityArithmetic.Resolve(new ContextCapacityInputs(ModelWindowTokens: 150_000));
        var selection = ContextCandidateSelector.Select(
            history,
            nonMessageTokens: 67_533,
            capacity,
            new CandidateSelectionOptions(ProtectedTailMessages: 8, TriggerRatio: 0.80, TargetRatio: 0.50),
            routeVersion: "deepseek/flash",
            toolSpecVersion: "tools-99",
            sourceGeneration: 12,
            sourceRevision: "rev-42");
        Assert.IsTrue(selection.Created, selection.Reason);
        Assert.IsNotEmpty(selection.RetainedSuffixIdentities);

        var frozen = new FrozenCandidateDescriptor(
            Fingerprint: selection.Fingerprint!,
            SourceGeneration: 12,
            SourceRevision: "rev-42",
            RouteVersion: "deepseek/flash",
            ToolSpecVersion: "tools-99",
            PolicyVersion: ContextPolicyVersion.Current,
            RemovedIdentities: selection.RemovedMessages.Select(m => m.Identity).ToArray(),
            RetainedIdentities: selection.RetainedSuffixIdentities);

        var current = new CurrentContextDescriptor(
            Generation: 12,
            Revision: "rev-42",
            RouteVersion: "deepseek/flash",
            ToolSpecVersion: "tools-99",
            PolicyVersion: ContextPolicyVersion.Current,
            MessageIdentities: history.Select(m => m.Identity).ToArray());

        var result = ContextCandidateApplicability.Validate(frozen, current);

        Assert.AreEqual(CandidateApplicabilityVerdict.Applicable, result.Verdict, result.Reason);

        // 反向控制：误用全量 RetainedMessages 身份会立刻退化（这正是被修掉的契约 bug）。
        var wrongDescriptor = frozen with
        {
            RetainedIdentities = selection.RetainedMessages.Select(m => m.Identity).ToArray(),
        };
        Assert.IsFalse(ContextCandidateApplicability.Validate(wrongDescriptor, current).IsApplicable);
    }
}
