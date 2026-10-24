using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// 思考强度决策的不变量（用户裁定 2026-10-24）：
/// ① high 是产品默认——没有任何来源表态时必须是 high，而不是留空让服务端决定；
/// ② 每轮偏好优先于配置；
/// ③ 「深入」只能映射 max（high 已是默认，映射到 high 等于没有深入）；
/// ④ 非法值不得进入请求，但必须被报告；
/// ⑤ 大小写与空白不影响判定。
/// </summary>
[TestClass]
public sealed class ReasoningEffortPolicyTests
{
    [TestMethod]
    public void DeepPreferenceMapsToMax()
    {
        var decision = ReasoningEffortPolicy.Resolve("deep", "low", "high");

        Assert.AreEqual(ReasoningEffortPolicy.Max, decision.Effort,
            "high 已是默认，深入若也映射 high 就等于没有深入。");
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
    public void AutoPrefersConfiguredOverModelDefault()
    {
        var decision = ReasoningEffortPolicy.Resolve("auto", "high", "low");

        Assert.AreEqual(ReasoningEffortPolicy.High, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.Configured, decision.Source);
    }

    [TestMethod]
    public void AutoWithoutConfiguredUsesModelDefault()
    {
        var decision = ReasoningEffortPolicy.Resolve(null, null, "max");

        Assert.AreEqual(ReasoningEffortPolicy.Max, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.ModelDefault, decision.Source);
    }

    [TestMethod]
    public void AutoWithNothingSpecifiedDefaultsToHigh()
    {
        var decision = ReasoningEffortPolicy.Resolve("auto", null, null);

        Assert.AreEqual(ReasoningEffortPolicy.High, decision.Effort,
            "产品默认是 high；留空等于把选择权交给服务端，那是不可观测的。");
        Assert.AreEqual(ReasoningEffortPolicy.Origin.ProductDefault, decision.Source);
        Assert.IsTrue(decision.IsSet);
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
    public void InvalidOnlyValueStillFallsBackToDefault()
    {
        var decision = ReasoningEffortPolicy.Resolve("auto", "HIGHEST", null);

        Assert.AreEqual(ReasoningEffortPolicy.High, decision.Effort);
        Assert.AreEqual(ReasoningEffortPolicy.Origin.ProductDefault, decision.Source);
        Assert.AreEqual("highest", decision.RejectedValue);
    }

    [TestMethod]
    public void NormalizesCaseAndWhitespace()
    {
        var decision = ReasoningEffortPolicy.Resolve("  DEEP ", "  HIGH  ", null);

        Assert.AreEqual(ReasoningEffortPolicy.Max, decision.Effort);
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
    public void ValidSetMatchesProviderVocabulary()
    {
        Assert.IsTrue(ReasoningEffortPolicy.IsValid(null));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid(""));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("none"));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("low"));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("high"));
        Assert.IsTrue(ReasoningEffortPolicy.IsValid("max"), "provider 提供的 max 必须被接受。");
        Assert.IsFalse(ReasoningEffortPolicy.IsValid("medium"),
            "provider 不提供 medium；旧代码的 medium 属虚构档位，必须被拒绝。");
        Assert.IsFalse(ReasoningEffortPolicy.IsValid("xhigh"));
        Assert.IsFalse(ReasoningEffortPolicy.IsValid("true"));
    }

    [TestMethod]
    public void ProductDefaultIsHigh()
    {
        Assert.AreEqual("high", ReasoningEffortPolicy.Default);
    }
}
