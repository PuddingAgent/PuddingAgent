using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// 思考强度决策的五条不变量：
/// ① 每轮偏好优先于配置；② auto 不表态时让配置生效；
/// ③ 非法值不得进入请求，但必须被报告；④ 全空时返回 null（不发该字段）；
/// ⑤ 大小写与空白不影响判定。
/// </summary>
[TestClass]
public sealed class ReasoningEffortPolicyTests
{
    [TestMethod]
    public void DeepPreferenceWinsOverConfigured()
    {
        var decision = ReasoningEffortPolicy.Resolve("deep", "low", "medium");

        Assert.AreEqual(ReasoningEffortPolicy.High, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.Preference, decision.Source);
    }

    [TestMethod]
    public void FastPreferenceMapsToLow()
    {
        var decision = ReasoningEffortPolicy.Resolve("fast", "high", "high");

        Assert.AreEqual(ReasoningEffortPolicy.Low, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.Preference, decision.Source);
    }

    [TestMethod]
    public void AutoFallsBackToConfigured()
    {
        var decision = ReasoningEffortPolicy.Resolve("auto", "medium", "high");

        Assert.AreEqual(ReasoningEffortPolicy.Medium, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.Configured, decision.Source);
    }

    [TestMethod]
    public void AutoWithoutConfiguredUsesModelDefault()
    {
        var decision = ReasoningEffortPolicy.Resolve(null, null, "minimal");

        Assert.AreEqual(ReasoningEffortPolicy.Minimal, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.ModelDefault, decision.Source);
    }

    [TestMethod]
    public void NothingSpecifiedLeavesFieldAbsent()
    {
        var decision = ReasoningEffortPolicy.Resolve("auto", null, null);

        Assert.IsNull(decision.Effort, "没有来源表态时不得伪造强度，必须让请求里不出现该字段。");
        Assert.AreEqual(ReasoningEffortPolicy.Origin.Unset, decision.Source);
        Assert.IsFalse(decision.IsSet);
    }

    [TestMethod]
    public void InvalidConfiguredIsRejectedAndReported()
    {
        var decision = ReasoningEffortPolicy.Resolve("auto", "hgiht", "low");

        Assert.AreEqual(ReasoningEffortPolicy.Low, decision.Effort, "非法配置必须回落到下一来源。");
        Assert.AreEqual(ReasoningEffortPolicy.Origin.ModelDefault, decision.Source);
        Assert.AreEqual("hgiht", decision.RejectedValue, "被拒绝的原值必须带回，否则日志里看不出退化过。");
    }

    [TestMethod]
    public void InvalidOnlyValueReportsRejection()
    {
        var decision = ReasoningEffortPolicy.Resolve("auto", "HIGHEST", null);

        Assert.IsNull(decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.Unset, decision.Source);
        Assert.AreEqual("highest", decision.RejectedValue);
    }

    [TestMethod]
    public void NormalizesCaseAndWhitespace()
    {
        var decision = ReasoningEffortPolicy.Resolve("  DEEP ", "  HIGH  ", null);

        Assert.AreEqual(ReasoningEffortPolicy.High, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.Preference, decision.Source);
    }

    [TestMethod]
    public void UnknownPreferenceBehavesLikeAuto()
    {
        var decision = ReasoningEffortPolicy.Resolve("turbo", "low", null);

        Assert.AreEqual(ReasoningEffortPolicy.Low, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.Configured, decision.Source);
    }

    [TestMethod]
    public void ValidSetCoversProviderVocabularyOnly()
    {
        Assert.IsTrue(ReasoningEffortPolicy.IsValid(null));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid(""));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("none"));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("minimal"));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("low"));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("medium"));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("high"));
        Assert.IsFalse(ReasoningEffortPolicy.IsValid("xhigh"));
        Assert.IsFalse(ReasoningEffortPolicy.IsValid("true"));
    }
}
