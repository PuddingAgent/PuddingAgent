using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// 思考强度决策的不变量（用户裁定 2026-10-24）：
/// ① 手动三档与强度一一对应：快速=low、深入=high、极致=max；
/// ② high 是产品默认——没有任何来源表态时必须是 high；
/// ③ 手动档位优先于配置；
/// ④ 非法值不得进入请求，但必须被报告；
/// ⑤ 大小写与空白不影响判定。
/// </summary>
[TestClass]
public sealed class ReasoningEffortPolicyTests
{
    [TestMethod]
    public void ManualLevelsMapOneToOne()
    {
        Assert.AreEqual("low", ReasoningEffortPolicy.Resolve("low", "max", null).Effort);
        Assert.AreEqual("high", ReasoningEffortPolicy.Resolve("high", "max", null).Effort);
        Assert.AreEqual("max", ReasoningEffortPolicy.Resolve("max", "low", null).Effort);
    }

    [TestMethod]
    public void ManualLevelBeatsConfigured()
    {
        var decision = ReasoningEffortPolicy.Resolve("high", "low", "low");

        Assert.AreEqual(ReasoningEffortPolicy.High, decision.Effort);
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
        var decision = ReasoningEffortPolicy.Resolve("  MAX ", "  HIGH  ", null);

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
            "provider 不提供 medium；它是虚构档位，必须被拒绝。");
        Assert.IsFalse(ReasoningEffortPolicy.IsValid("xhigh"));
    }

    [TestMethod]
    public void LabelsMatchTheThreeManualLevels()
    {
        Assert.AreEqual("快速", ReasoningEffortPolicy.Label("low"));
        Assert.AreEqual("深入", ReasoningEffortPolicy.Label("high"));
        Assert.AreEqual("极致", ReasoningEffortPolicy.Label("max"));
        Assert.AreEqual("high", ReasoningEffortPolicy.Default);
    }
}
