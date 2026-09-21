using PuddingCode.Operators;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// S2a 契约测试：逐标签验收门槛（<b>单侧</b>）的判定边界、缺失保守语义、构造期校验，
/// 以及「可版本化判据」被抬成类型事实（三区间与单侧门共用标识面，但形状<b>不</b>互相塞入）。
/// <para>
/// 为什么这些断言值得单独立测：判定谓词一旦被误改成阈值兜底 / 区间语义，闸门就可能在
/// 置信度缺失时静默放行——这类错误在集成测试里通常表现为「偶发放行」，极难回溯。
/// </para>
/// </summary>
[TestClass]
public sealed class AcceptanceThresholdPolicyTests
{
    private const double Required = 0.90d;

    [TestMethod]
    public void IsMet_InclusiveBoundary_AtOrAboveRequired_IsMet()
    {
        var policy = AcceptanceThresholdPolicy.Create("test.required", 1, Required);

        Assert.IsTrue(policy.IsMet(1.0d), "高于门槛 ⇒ 达标。");
        Assert.IsTrue(policy.IsMet(Required), "边界必须含等（对齐既有 >= 语义）。");
        Assert.IsTrue(policy.IsMet(0.9000001d));
        Assert.IsFalse(policy.IsMet(0.8999999d), "低于门槛一位也不得达标。");
        Assert.IsFalse(policy.IsMet(0d), "0 只是合法置信度，不是达标值。");
    }

    [TestMethod]
    public void IsMet_MissingConfidence_IsFalse_Conservative()
    {
        var policy = AcceptanceThresholdPolicy.Create("test.required", 1, Required);

        Assert.IsFalse(policy.IsMet(null), "缺失置信度必须保守判为不达门槛（绝不放行）。");
    }

    [TestMethod]
    public void IsMet_NonFiniteOrOutOfRangeInput_NeverPasses_AndNeverThrows()
    {
        var policy = AcceptanceThresholdPolicy.Create("test.required", 1, Required);

        Assert.IsFalse(policy.IsMet(double.NaN), "NaN 输入不得抛异常（闸门谓词不得因坏输入而中断），也不得放行。");
        Assert.IsFalse(policy.IsMet(double.NegativeInfinity));
        Assert.IsTrue(
            policy.IsMet(1.5d),
            "输入不做 [0,1] 夹取：既有实现是裸 >=，夹取会改变输入语义（越界输入一律拒绝受理不在本切片范围）。");
    }

    [TestMethod]
    public void IsMet_HandMadeIllegalThreshold_FailsClosedForEveryInput()
    {
        var illegal = new AcceptanceThresholdPolicy
        {
            PolicyId = "test.illegal",
            Version = 1,
            RequiredConfidence = double.NaN,
        };

        Assert.IsFalse(illegal.IsMet(null));
        Assert.IsFalse(illegal.IsMet(1d), "手搓非法门槛时任何输入都不得判为达标（失败方向永远是不放行）。");
    }

    [TestMethod]
    public void Create_RejectsIllegalValues_AtConstructionTime()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AcceptanceThresholdPolicy.Create("p", 1, double.NaN), "NaN 门槛必须在构造期拒绝。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AcceptanceThresholdPolicy.Create("p", 1, -0.0001d), "负门槛必须在构造期拒绝。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AcceptanceThresholdPolicy.Create("p", 1, 1.0001d), "超 1 门槛必须在构造期拒绝。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => AcceptanceThresholdPolicy.Create("p", 0, 0.9d), "版本必须 >= 1。");
        Assert.ThrowsExactly<ArgumentException>(
            () => AcceptanceThresholdPolicy.Create("   ", 1, 0.9d), "无 id 的判据无法落库、无法事后解释。");

        // 边界合法值必须放行：0 与 1 都是合法门槛。
        Assert.AreEqual(0d, AcceptanceThresholdPolicy.Create("p", 1, 0d).RequiredConfidence, 1e-12);
        Assert.AreEqual(1d, AcceptanceThresholdPolicy.Create("p", 1, 1d).RequiredConfidence, 1e-12);
    }

    [TestMethod]
    public void Create_PreservesOptionalFields_AndDefaultsLabelsToEmpty()
    {
        var policy = AcceptanceThresholdPolicy.Create(
            "p.id",
            3,
            0.5d,
            appliesToLabels: ["allow_permanent", "deny_permanent"],
            sceneKey: "scene-1",
            note: "note-1");

        Assert.AreEqual("p.id", policy.PolicyId);
        Assert.AreEqual(3, policy.Version);
        Assert.AreEqual(0.5d, policy.RequiredConfidence, 1e-12);
        Assert.AreEqual(2, policy.AppliesToLabels.Count);
        Assert.AreEqual("allow_permanent", policy.AppliesToLabels[0]);
        Assert.AreEqual("scene-1", policy.SceneKey);
        Assert.AreEqual("note-1", policy.Note);

        var minimal = AcceptanceThresholdPolicy.Create("p.id", 1, 0.5d);
        Assert.AreEqual(0, minimal.AppliesToLabels.Count, "未指定标签 ⇒ 空集 = 不限定标签。");
        Assert.IsNull(minimal.SceneKey);
        Assert.IsNull(minimal.Note);
    }

    [TestMethod]
    public void BothCriterionShapes_ShareOnlyTheIdentityFace()
    {
        // 纯增量：三区间策略也实现 IVersionedCriterion（标识面共用），但判定形状各自独立。
        IVersionedCriterion threeRegion = ThresholdPolicy.Create("three.region", 2, 0.8d, 0.2d);
        IVersionedCriterion singleSide = AcceptanceThresholdPolicy.Create("single.side", 2, Required);

        Assert.AreEqual("three.region", threeRegion.PolicyId);
        Assert.AreEqual(2, threeRegion.Version);
        Assert.AreEqual("single.side", singleSide.PolicyId);
        Assert.AreEqual(2, singleSide.Version);

        // 三区间语义未被 S2a 触碰：边界含等仍在原处，Abstain 带仍是开区间。
        var three = (ThresholdPolicy)threeRegion;
        Assert.AreEqual(JudgeOutcome.Yes, three.Apply(0.8d));
        Assert.AreEqual(JudgeOutcome.Abstain, three.Apply(0.5d));
        Assert.AreEqual(JudgeOutcome.No, three.Apply(0.2d));

        // 单侧门绝不产出第三态：只有「达标 / 不达标」，不存在 Abstain。
        var single = (AcceptanceThresholdPolicy)singleSide;
        Assert.IsTrue(single.IsMet(0.9d));
        Assert.IsFalse(single.IsMet(0.8999d));
    }
}
