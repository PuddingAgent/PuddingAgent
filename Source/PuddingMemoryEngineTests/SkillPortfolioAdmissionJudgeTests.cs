using PuddingCode.Skills.Portfolio;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// G3 组合预算（判断器）契约用例。
/// <para>
/// 纪律：每个用例断言**具体不变量**（动作、目标、快照），不得只断言"没有抛异常"。
/// 每个用例都必须能在对应实现被改坏时**变红**（见 <c>RSI-G3-任务书-2026-09-22.md</c> §5 的 I1–I8）。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillPortfolioAdmissionJudgeTests
{
    // ───────────────────────── I7 配置合法性 fail-closed ─────────────────────────

    [TestMethod]
    public void Policy_ShouldRejectDegenerateConfiguration_AtConstructionTime()
    {
        // 硬区间小于软区间 ⇒ 软区间恒为空（阶梯不存在），必须在构造期就被拒绝。
        var degenerate = Assert.ThrowsExactly<InvalidOperationException>(
            () => SkillPortfolioPolicy.Create("p", 1, hardCap: 4, softTarget: 9, perFamilyCap: null, minMarginalGain: 0.05, stalenessDays: 90));
        StringAssert.Contains(degenerate.Message, "HardCap");

        Assert.ThrowsExactly<InvalidOperationException>(
            () => SkillPortfolioPolicy.Create("p", 1, hardCap: 10, softTarget: 5, perFamilyCap: null, minMarginalGain: -0.01, stalenessDays: 90));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SkillPortfolioPolicy.Create("p", 1, hardCap: 10, softTarget: -1, perFamilyCap: null, minMarginalGain: 0.05, stalenessDays: 90));

        // 合法配置不得抛。
        Assert.IsTrue(Policy().IsValid);
    }

    // ───────────────────────── 行 1：软目标以下零回归 ─────────────────────────

    [TestMethod]
    public void BelowSoftTarget_ShouldPassThroughDedupResult_EvenWhenBudgetFieldsAreUnavailable()
    {
        var dedup = CreateResult();
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5));

        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = 4,
            DedupResult = dedup,
            CandidateScore = null,
            MinEnabledScore = null,
        });

        Assert.AreSame(dedup, result, "软目标以下必须原样透传既有去重结论（零回归区间）。");
        Assert.AreEqual(SkillAdmissionActions.Create, result.Action);
    }

    // ───────────────────────── I3 降级单调性（no-upgrade） ─────────────────────────

    [DataTestMethod]
    [DataRow(SkillAdmissionActions.Merge, 0)]
    [DataRow(SkillAdmissionActions.Skip, 4)]
    [DataRow(SkillAdmissionActions.Defer, 9)]
    [DataRow(SkillAdmissionActions.Merge, 10)]
    public void NoUpgrade_ShouldNeverTurnNonCreateIntoCreate(string dedupAction, int enabledCount)
    {
        var dedup = new SkillAdmissionResult
        {
            Action = dedupAction,
            TargetSkillId = "other",
            Confidence = 0.9,
            Reason = "from-dedup",
        };
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5));

        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = enabledCount,
            DedupResult = dedup,
            CandidateScore = Observed(0.99),
            MinEnabledScore = Observed(0.10),
            MinEnabledSkillId = "lowest",
        });

        Assert.AreSame(dedup, result, "预算层只能收紧，永不得把非 create 的结论提升为 create。");
        Assert.AreEqual(dedupAction, result.Action);
    }

    // ───────────────────────── I2 预算不可被 LLM 越过 ─────────────────────────

    [TestMethod]
    public void BudgetFull_WithInsufficientMargin_ShouldDefer_NotCreate()
    {
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5, minMarginalGain: 0.05));

        // "LLM 要求 create"，但预算已满且候选项并不比最低分好出所需边际。
        var result = judge.Apply(FullBudgetInput(candidateScore: 0.60, minEnabledScore: 0.58, minEnabledSkillId: "weakest"));

        Assert.AreEqual(SkillAdmissionActions.Defer, result.Action, "预算已满且边际不足 ⇒ 不得 create。");
        Assert.IsNull(result.TargetSkillId);
        StringAssert.Contains(result.Reason!, "insufficient_margin_at_hard_cap");
        StringAssert.Contains(result.Reason!, Policy().PolicyId);
    }

    [TestMethod]
    public void BudgetFull_WithSufficientMargin_ShouldDisplaceLowestValue()
    {
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5, minMarginalGain: 0.05));

        var result = judge.Apply(FullBudgetInput(candidateScore: 0.90, minEnabledScore: 0.20, minEnabledSkillId: "weakest"));

        Assert.AreEqual(SkillAdmissionActions.Displace, result.Action);
        Assert.AreEqual("weakest", result.TargetSkillId, "置换目标必须是价值最低者。");
        StringAssert.Contains(result.Reason!, "displace_lowest_value");
    }

    [TestMethod]
    public void MarginEqualToThreshold_ShouldNotAdmit_BecauseComparisonIsStrict()
    {
        // 阈值与分数都取二进制可精确表示的值（0.25 / 0.5 / 0.75），否则“恰好相等”会因浮点误差
        // 变成“大于”，边界用例就失去断言能力（首版用 0.55-0.50=0.05 即因此变红）。
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5, minMarginalGain: 0.25));

        // margin 恰好等于阈值（0.75 - 0.50 = 0.25）⇒ 契约要求“大于”，故必须延后。
        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = 7,
            DedupResult = CreateResult(),
            CandidateScore = Observed(0.75),
            MinEnabledScore = Observed(0.50),
            MinEnabledSkillId = "weakest",
        });

        Assert.AreEqual(SkillAdmissionActions.Defer, result.Action);
        StringAssert.Contains(result.Reason!, "insufficient_margin_above_soft_target");
    }

    [TestMethod]
    public void HardCapWithoutDisplaceTarget_ShouldDefer_FailClosed()
    {
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5, minMarginalGain: 0.05));

        // 分数够置换，但拿不到"最低分持有者" ⇒ 必须 fail-closed 延后，否则硬上限形同虚设。
        var result = judge.Apply(FullBudgetInput(candidateScore: 0.90, minEnabledScore: 0.20, minEnabledSkillId: null));

        Assert.AreEqual(SkillAdmissionActions.Defer, result.Action);
        StringAssert.Contains(result.Reason!, "displace_target_missing");
    }

    // ───────────────────────── I1 冷启动只降不杀 ─────────────────────────

    [TestMethod]
    public void ColdStart_ShouldDefer_AndNeverDisplace()
    {
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5));

        var unavailable = new SkillScoreSnapshot.Unavailable("skill_value_insufficient_data");
        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = 10,
            DedupResult = CreateResult(),
            CandidateScore = unavailable,
            MinEnabledScore = unavailable,
            MinEnabledSkillId = "weakest",
        });

        Assert.AreEqual(SkillAdmissionActions.Defer, result.Action, "冷启动：无数据 ≠ 低价值 ⇒ 只降不杀。");
        StringAssert.Contains(result.Reason!, "candidate_score_unavailable");
        Assert.IsFalse(unavailable.IsComparable, "无数据快照不参与比较（类型级约束）。");
    }

    [TestMethod]
    public void MinEnabledScoreUnavailable_ShouldDefer_BecauseRankingIsImpossible()
    {
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5));

        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = 10,
            DedupResult = CreateResult(),
            CandidateScore = Observed(0.99),
            MinEnabledScore = new SkillScoreSnapshot.Unavailable("skill_value_insufficient_data"),
            MinEnabledSkillId = "weakest",
        });

        Assert.AreEqual(SkillAdmissionActions.Defer, result.Action);
        StringAssert.Contains(result.Reason!, "min_enabled_score_unavailable");
    }

    [TestMethod]
    public void ScoreScaleMismatch_ShouldDefer_BecauseSubtractingDifferentUnitsIsMeaningless()
    {
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5));

        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = 10,
            DedupResult = CreateResult(),
            CandidateScore = Observed(0.99, "skill_value_v1"),
            MinEnabledScore = Observed(0.20, "some_other_scale"),
            MinEnabledSkillId = "weakest",
        });

        Assert.AreEqual(SkillAdmissionActions.Defer, result.Action);
        StringAssert.Contains(result.Reason!, "score_scale_mismatch");
    }

    // ───────────────────────── 行 2：软区间（正/负） ─────────────────────────

    [TestMethod]
    public void SoftRegion_WithSufficientMargin_ShouldAdmit()
    {
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5, minMarginalGain: 0.05));
        var dedup = CreateResult();

        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = 7,
            DedupResult = dedup,
            CandidateScore = Observed(0.80),
            MinEnabledScore = Observed(0.20),
            MinEnabledSkillId = "weakest",
        });

        Assert.AreSame(dedup, result);
        Assert.AreEqual(SkillAdmissionActions.Create, result.Action);
    }

    [TestMethod]
    public void SoftRegion_WithInsufficientMargin_ShouldDefer()
    {
        var judge = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5, minMarginalGain: 0.05));

        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = 7,
            DedupResult = CreateResult(),
            CandidateScore = Observed(0.30),
            MinEnabledScore = Observed(0.90),
            MinEnabledSkillId = "weakest",
        });

        Assert.AreEqual(SkillAdmissionActions.Defer, result.Action);
        StringAssert.Contains(result.Reason!, "insufficient_margin_above_soft_target");
    }

    // ───────────────────────── I5 阈值全部来自策略对象 ─────────────────────────

    [TestMethod]
    public void Thresholds_ShouldComeFromPolicyInstance_NotFromHardCodedValues()
    {
        var input = new SkillPortfolioInput
        {
            EnabledCount = 7,
            DedupResult = CreateResult(),
            CandidateScore = Observed(0.60),
            MinEnabledScore = Observed(0.50),   // margin = 0.10
            MinEnabledSkillId = "weakest",
        };

        var permissive = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5, minMarginalGain: 0.05));
        var strict = new SkillPortfolioAdmissionJudge(Policy(hardCap: 10, softTarget: 5, minMarginalGain: 0.20));

        Assert.AreEqual(SkillAdmissionActions.Create, permissive.Apply(input).Action, "阈值 0.05 < margin 0.10 ⇒ 准入。");
        Assert.AreEqual(SkillAdmissionActions.Defer, strict.Apply(input).Action, "阈值 0.20 > margin 0.10 ⇒ 延后。");
    }

    [TestMethod]
    public void AppliedSnapshot_ShouldCarryExactlyTheConsumedThresholds()
    {
        var policy = Policy(hardCap: 12, softTarget: 6, minMarginalGain: 0.07);

        var applied = policy.ToApplied();

        Assert.AreEqual(policy.PolicyId, applied.PolicyId);
        Assert.AreEqual(policy.Version, applied.Version);
        Assert.AreEqual(12, applied.HardCap);
        Assert.AreEqual(6, applied.SoftTarget);
        Assert.AreEqual(0.07, applied.MinMarginalGain, 0.0001);
    }

    // ───────────────────────── S4 过渡默认值零回归 ─────────────────────────

    [TestMethod]
    public void ZeroRegressionDefault_ShouldKeepTheGateAboveTheCurrentSize()
    {
        var policy = SkillPortfolioPolicy.ZeroRegressionDefault(currentEnabledCount: 40, headroom: 20);

        Assert.AreEqual(60, policy.SoftTarget);
        Assert.AreEqual(80, policy.HardCap);
        Assert.IsTrue(policy.IsValid);

        // 现状（40）与现状之上直到软目标之前，都必须完全透传既有去重结论。
        var judge = new SkillPortfolioAdmissionJudge(policy);
        var dedup = CreateResult();
        var result = judge.Apply(new SkillPortfolioInput
        {
            EnabledCount = 59,
            DedupResult = dedup,
            CandidateScore = new SkillScoreSnapshot.Unavailable("skill_value_insufficient_data"),
            MinEnabledScore = new SkillScoreSnapshot.Unavailable("skill_value_insufficient_data"),
        });

        Assert.AreSame(dedup, result);
    }

    [TestMethod]
    public void ZeroRegressionDefault_ShouldRejectNegativeMeasuredSize()
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SkillPortfolioPolicy.ZeroRegressionDefault(currentEnabledCount: -1));

    // ───────────────────────── helpers ─────────────────────────

    // ───────────────────────── G4-D2：PerFamilyCap 语义（int? / 拒 0 与负） ─────────────────────────

    [TestMethod]
    public void PerFamilyCap_ShouldRejectZeroAndNegative_ButAcceptNullAndPositive()
    {
        // 0 会让上限对任何非空家族恒超限（外表像"关闭了该判据"，实为"全禁"）；负值无意义。
        Assert.ThrowsExactly<InvalidOperationException>(() => Policy(perFamilyCap: 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => Policy(perFamilyCap: -1));

        // null = 显式"不设限"；正数 = 真上限。
        Assert.IsTrue(Policy(perFamilyCap: null).IsValid);
        Assert.IsTrue(Policy(perFamilyCap: 1).IsValid);
    }

    [TestMethod]
    public void ZeroRegressionDefault_ShouldLeaveFamilyCapUnset()
    {
        // 默认策略的每家族上限必须是 null（不设限），不得用 0 当哨兵值。
        // 139 = 只读盘点报告实测的启用技能数基线，用于确保本断言不依赖具体数字。
        Assert.IsNull(SkillPortfolioPolicy.ZeroRegressionDefault(currentEnabledCount: 139).PerFamilyCap);
    }

    [TestMethod]
    public void Judge_ShouldIgnorePerFamilyCap_UntilFamilyCapsAreWired()
    {
        // 载有 ≠ 生效：本切片只定义语义、不消费 ⇒ 仅 PerFamilyCap 不同的两个策略必须给出**同一**判定。
        // ⚠️ 本用例是闸门：后续交付物让家族上限真正生效时，必须在同一提交里改掉它（不得静默放宽）。
        var uncapped = new SkillPortfolioAdmissionJudge(Policy(perFamilyCap: null))
            .Apply(FullBudgetInput(candidateScore: 0.90, minEnabledScore: 0.20, minEnabledSkillId: "weakest"));
        var capped = new SkillPortfolioAdmissionJudge(Policy(perFamilyCap: 1))
            .Apply(FullBudgetInput(candidateScore: 0.90, minEnabledScore: 0.20, minEnabledSkillId: "weakest"));

        Assert.AreEqual(uncapped.Action, capped.Action);
        Assert.AreEqual(uncapped.TargetSkillId, capped.TargetSkillId);
        Assert.AreEqual(uncapped.Reason, capped.Reason);
    }

    [TestMethod]
    public void AppliedSnapshot_ShouldNotClaimPerFamilyCap_UntilItIsConsumed()
    {
        // 防幻影区间：配置里有值 ≠ 已生效。快照字段集合必须**恰好**是本次判定实际消费的三个阈值。
        // ⚠️ 本用例是闸门：家族上限被真正消费时，必须与"把字段加进快照"在同一提交里更新本用例。
        var names = typeof(AppliedPortfolioPolicy).GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "HardCap", "MinMarginalGain", "PolicyId", "SoftTarget", "Version" }, names);
    }

    private static SkillPortfolioPolicy Policy(int hardCap = 10, int softTarget = 5, double minMarginalGain = 0.05, int? perFamilyCap = null)
        => SkillPortfolioPolicy.Create("skill-portfolio/test", 1, hardCap, softTarget, perFamilyCap, minMarginalGain, stalenessDays: 90);

    private static SkillAdmissionResult CreateResult()
        => new()
        {
            Action = SkillAdmissionActions.Create,
            Confidence = 0.99,
            Reason = "from-dedup",
        };

    private static SkillScoreSnapshot.Observed Observed(double score, string scale = "skill_value_v1") => new(scale, score);

    private static SkillPortfolioInput FullBudgetInput(double candidateScore, double minEnabledScore, string? minEnabledSkillId)
        => new()
        {
            EnabledCount = 10,   // == HardCap
            DedupResult = CreateResult(),
            CandidateScore = Observed(candidateScore),
            MinEnabledScore = Observed(minEnabledScore),
            MinEnabledSkillId = minEnabledSkillId,
        };
}
