using System.Reflection;
using System.Runtime.CompilerServices;
using PuddingCode.Operators;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// S1a 契约测试：阈值三区间 / 必填字段 / 三投影共享信封 / <see cref="JudgeOutcome.Abstain"/> 不被折叠。
/// </summary>
[TestClass]
public sealed class OperatorContractTests
{
    [TestMethod]
    public void ThresholdPolicy_Apply_UsesThreeRegions_WithInclusiveBoundaries()
    {
        var policy = OperatorTestData.Policy(yes: 0.8d, no: 0.2d);

        // YesAtOrAbove 侧：含等
        Assert.AreEqual(JudgeOutcome.Yes, policy.Apply(1.0d));
        Assert.AreEqual(JudgeOutcome.Yes, policy.Apply(0.8d));
        Assert.AreEqual(JudgeOutcome.Yes, policy.Apply(0.8000001d));

        // 两阈值之间：Abstain（开区间）
        Assert.AreEqual(JudgeOutcome.Abstain, policy.Apply(0.7999999d));
        Assert.AreEqual(JudgeOutcome.Abstain, policy.Apply(0.5d));
        Assert.AreEqual(JudgeOutcome.Abstain, policy.Apply(0.2000001d));

        // NoAtOrBelow 侧：含等
        Assert.AreEqual(JudgeOutcome.No, policy.Apply(0.2d));
        Assert.AreEqual(JudgeOutcome.No, policy.Apply(0.1999999d));
        Assert.AreEqual(JudgeOutcome.No, policy.Apply(0.0d));
        Assert.AreEqual(JudgeOutcome.No, policy.Apply(-1.0d));
    }

    [TestMethod]
    public void ThresholdPolicy_InvalidConfiguration_IsRejected_NotSilentlyAllYesOrAllAbstain()
    {
        var inverted = new ThresholdPolicy { PolicyId = "p-inverted", Version = 1, YesAtOrAbove = 0.2d, NoAtOrBelow = 0.8d };
        Assert.IsFalse(inverted.IsValid, "YesAtOrAbove <= NoAtOrBelow 必须判为非法配置。");
        Assert.ThrowsExactly<InvalidOperationException>(() => inverted.Apply(0.5d));
        Assert.ThrowsExactly<InvalidOperationException>(() => inverted.EnsureValid());
        Assert.ThrowsExactly<InvalidOperationException>(() => inverted.ToApplied());
        Assert.ThrowsExactly<InvalidOperationException>(() => ThresholdPolicy.Create("p-inverted", 1, 0.2d, 0.8d));

        var degenerate = new ThresholdPolicy { PolicyId = "p-equal", Version = 1, YesAtOrAbove = 0.5d, NoAtOrBelow = 0.5d };
        Assert.IsFalse(degenerate.IsValid, "两阈值相等会退化为无 Abstain 带，必须判为非法。");
        Assert.ThrowsExactly<InvalidOperationException>(() => degenerate.Apply(0.5d));

        // 合法配置：Create 通过且 IsValid
        Assert.IsTrue(OperatorTestData.Policy().IsValid);
    }

    [TestMethod]
    public void ThresholdPolicy_NaNScore_IsRejected_InsteadOfSilentlyFallingIntoARegion()
    {
        var policy = OperatorTestData.Policy();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => policy.Apply(double.NaN));
    }

    [TestMethod]
    public void JudgementEnvelope_IdentityFields_AreRequired()
    {
        string[] requiredNames =
        [
            nameof(JudgementEnvelope.JudgementId),
            nameof(JudgementEnvelope.SceneKey),
            nameof(JudgementEnvelope.InputDigest),
            nameof(JudgementEnvelope.OperatorId),
            nameof(JudgementEnvelope.SchemaVersion),
            nameof(JudgementEnvelope.ConfidenceKind),
            nameof(JudgementEnvelope.Reason),
            nameof(JudgementEnvelope.CreatedAtUtc),
        ];

        foreach (var name in requiredNames)
        {
            var property = typeof(JudgementEnvelope).GetProperty(name);
            Assert.IsNotNull(property, $"{name} 必须存在。");
            Assert.IsNotNull(
                property!.GetCustomAttribute<RequiredMemberAttribute>(),
                $"{name} 必须声明为 required（缺字段必须在编译期被拒绝）。");
        }

        // 一个最小合法构造样例：只填必填字段也能构造（其余字段有安全默认值）。
        var envelope = new JudgementEnvelope
        {
            JudgementId = "jdg-sample",
            SceneKey = "probe-scene",
            InputDigest = "digest-1",
            OperatorId = "probe",
            SchemaVersion = JudgementEnvelope.CurrentSchemaVersion,
            ConfidenceKind = ConfidenceKind.Unknown,
            Reason = "样例",
            CreatedAtUtc = OperatorTestData.Origin,
        };

        Assert.AreEqual("jdg-sample", envelope.JudgementId);
        Assert.AreEqual(1, envelope.SchemaVersion);
        Assert.IsEmpty(envelope.Evidence);
        Assert.IsFalse(envelope.Cached);
        Assert.IsNull(envelope.Outcome);
        Assert.IsNull(envelope.Threshold);
    }

    [TestMethod]
    public void ThreeProjections_ShareOneEnvelope_AndKeepFieldsConsistent()
    {
        var scope = OperatorTestData.Scope();
        var envelope = scope.BuildEnvelope(new EnvelopeDraft
        {
            Reason = "投影一致性样例",
            Outcome = JudgeOutcome.Yes,
            Score = 0.9d,
            ScoreScale = "0..1",
            PrimaryLabel = "risk",
            LabelDistribution = new Dictionary<string, double> { ["risk"] = 0.9d },
            Confidence = 0.9d,
            ConfidenceKind = ConfidenceKind.Calibrated,
        });

        var score = new ScoreResult(envelope.ScoreScale!, envelope.Score!.Value, envelope);
        var judge = new JudgeResult(envelope.Outcome!.Value, envelope.Score, envelope.Threshold, envelope.Confidence, envelope.ConfidenceKind, envelope);
        var classification = new ClassificationResult(envelope.PrimaryLabel, envelope.LabelDistribution!, envelope);

        Assert.AreSame(envelope, score.Envelope);
        Assert.AreSame(envelope, judge.Envelope);
        Assert.AreSame(envelope, classification.Envelope);

        Assert.AreEqual(0.9d, score.Score);
        Assert.AreEqual(JudgeOutcome.Yes, judge.Outcome);
        Assert.AreEqual("risk", classification.PrimaryLabel);
        Assert.AreEqual(0.9d, classification.Distribution["risk"]);

        Assert.AreEqual(envelope.JudgementId, score.Envelope.JudgementId);
        Assert.AreEqual(envelope.JudgementId, judge.Envelope.JudgementId);
        Assert.AreEqual(envelope.JudgementId, classification.Envelope.JudgementId);
        Assert.AreEqual(JudgementEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
    }

    [TestMethod]
    public void Projections_DoNotLeakCrossSemantics()
    {
        // 输出语义必须由类型强制分离：打分不暴露结论、判断不暴露标签、分类不暴露通过与否。
        Assert.IsNull(typeof(ScoreResult).GetProperty(nameof(JudgeResult.Outcome)), "打分投影不得暴露 Outcome。");
        Assert.IsNull(typeof(ScoreResult).GetProperty(nameof(ClassificationResult.PrimaryLabel)), "打分投影不得暴露标签。");
        Assert.IsNull(typeof(JudgeResult).GetProperty(nameof(ClassificationResult.PrimaryLabel)), "判断投影不得暴露标签。");
        Assert.IsNull(typeof(ClassificationResult).GetProperty(nameof(JudgeResult.Outcome)), "分类投影不得暴露结论。");
        Assert.IsNull(typeof(ClassificationResult).GetProperty(nameof(ScoreResult.Score)), "分类投影不得暴露分数。");
    }

    [TestMethod]
    public void Abstain_IsNeverFolded_IntoYesOrNo()
    {
        var policy = OperatorTestData.Policy(yes: 0.8d, no: 0.2d);

        var abstained = policy.Apply(0.5d);
        Assert.AreEqual(JudgeOutcome.Abstain, abstained);
        Assert.AreNotEqual(JudgeOutcome.Yes, abstained);
        Assert.AreNotEqual(JudgeOutcome.No, abstained);

        // Abstain 不是 Unknown（降级），也不是默认值
        Assert.AreEqual(3, (int)JudgeOutcome.Abstain);
        Assert.AreEqual(0, (int)JudgeOutcome.Unknown);
        Assert.AreNotEqual(JudgeOutcome.Unknown, abstained);
        Assert.AreNotEqual(JudgeOutcome.Abstain, default(JudgeOutcome));

        // 经过信封与投影后仍不得被折叠
        var scope = OperatorTestData.Scope();
        var envelope = scope.BuildEnvelope(new EnvelopeDraft
        {
            Reason = "无法判定，弃权",
            Outcome = JudgeOutcome.Abstain,
            Score = 0.5d,
            ScoreScale = "0..1",
        });
        Assert.AreEqual(JudgeOutcome.Abstain, envelope.Outcome);

        var judge = new JudgeResult(JudgeOutcome.Abstain, 0.5d, envelope.Threshold, null, ConfidenceKind.Unknown, envelope);
        Assert.AreEqual(JudgeOutcome.Abstain, judge.Outcome);
        Assert.AreEqual(JudgeOutcome.Abstain, judge.Envelope.Outcome);
        Assert.AreNotEqual(JudgeOutcome.Yes, judge.Outcome);
        Assert.AreNotEqual(JudgeOutcome.No, judge.Outcome);
    }

    [TestMethod]
    public void OperatorScope_BuildEnvelope_FillsRuntimeFacts_AndJudgementIdIsDeterministic()
    {
        var clock = new FixedClock(OperatorTestData.Origin);
        var scope = OperatorTestData.Scope(clock: clock);
        clock.Advance(TimeSpan.FromMilliseconds(125));

        var envelope = scope.BuildEnvelope(new EnvelopeDraft { Reason = "样例", Score = 0.5d, ScoreScale = "0..1" });

        Assert.AreEqual(scope.JudgementId, envelope.JudgementId);
        Assert.AreEqual("probe-scene", envelope.SceneKey);
        Assert.AreEqual("digest-1", envelope.InputDigest);
        Assert.AreEqual(7, envelope.InstructionVersion);
        Assert.AreEqual(scope.OperatorVersion, envelope.OperatorVersion);
        Assert.AreEqual("policy-1", envelope.Threshold!.PolicyId);
        Assert.AreEqual(125d, envelope.LatencyMs ?? -1d);
        Assert.IsNotNull(envelope.Identity);
        Assert.AreEqual("ws-1", envelope.Identity!.WorkspaceId);
        Assert.AreEqual(JudgementEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);

        // 同一输入 ⇒ 同一判定 id（可复现 / 可去重）
        var other = OperatorTestData.Scope(clock: new FixedClock(OperatorTestData.Origin));
        Assert.AreEqual(scope.JudgementId, other.JudgementId);
        Assert.AreNotEqual(scope.JudgementId, OperatorTestData.Scope(inputDigest: "digest-2").JudgementId);

        var expected = OperatorScope.BuildJudgementId(envelope.OperatorId, envelope.OperatorVersion, 7, "digest-1");
        Assert.AreEqual(expected, envelope.JudgementId);
    }
}
