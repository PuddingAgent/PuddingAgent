using PuddingCode.Platform;
using PuddingCode.Skills.Family;
using PuddingCode.Skills.Portfolio;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// G4 交付物 D7「最薄接线」：家族评审接线的**判据侧**契约。
/// <para>
/// 分工（沿用 G7 的接线纪律）：本文件证明「接线把输入喂对、把计数算对、默认不改变任何既有行为」；
/// 作业结果层的可观测性由 <c>PuddingRuntimeTests</c> 的 <c>SkillCurate_JobResult_*</c> 用例证明
/// （只写日志不算「被记录」）。D3 已证明判据本身可红，本文件不重复。
/// </para>
/// <para>
/// I8 零回归的**实现点**：<see cref="SubconsciousOrchestrator.EvaluateFamilyReviews"/> 在两个策略任一为
/// <c>null</c> 时返回零值结论 ⇒ 未提供策略时编排器的报告与本切片之前逐字段相同。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillFamilyReviewWiringTests
{
    // ───────────── I8 零回归：默认（不传策略）必须零评审 ─────────────

    [TestMethod]
    public void WithoutAnyPolicy_ShouldReturnZeroReviews_AndEvaluateNothing()
    {
        var (evaluated, reviews) = SubconsciousOrchestrator.EvaluateFamilyReviews(Corpus(), null, null);

        Assert.AreEqual(0, evaluated, "未传策略 ⇒ 连家族都不划分（不得留下「算了但不生效」的中间态）。");
        Assert.AreEqual(0, reviews);
    }

    [TestMethod]
    public void WithOnlyOneOfTheTwoPolicies_ShouldReturnZeroReviews()
    {
        // 「半套策略」不得产生半套结论：只给划分或只给上限都必须 ⇒ 零评审。
        // 若这里放行，调用方会以为「上限生效了」，而实际上限根本没参与判定 —— 静默缺陷。
        var (familyOnlyEvaluated, familyOnlyReviews) =
            SubconsciousOrchestrator.EvaluateFamilyReviews(Corpus(), FamilyPolicy(), null);
        var (capOnlyEvaluated, capOnlyReviews) =
            SubconsciousOrchestrator.EvaluateFamilyReviews(Corpus(), null, PortfolioPolicy(perFamilyCap: 1));

        Assert.AreEqual(0, familyOnlyEvaluated);
        Assert.AreEqual(0, familyOnlyReviews);
        Assert.AreEqual(0, capOnlyEvaluated);
        Assert.AreEqual(0, capOnlyReviews);
    }

    [TestMethod]
    public void NullSkills_ShouldBeRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => SubconsciousOrchestrator.EvaluateFamilyReviews(
                null!,
                FamilyPolicy(),
                PortfolioPolicy(perFamilyCap: 1)));
    }

    // ───────────── 超限家族必须真的被计数 ─────────────

    [TestMethod]
    public void OverCapFamily_ShouldProduceExactlyOneReviewRequest()
    {
        var (evaluated, reviews) = SubconsciousOrchestrator.EvaluateFamilyReviews(
            Corpus(),
            FamilyPolicy(),
            PortfolioPolicy(perFamilyCap: 1));

        Assert.AreEqual(2, evaluated, "3 条同族 + 1 条孤立 ⇒ 2 个家族（含单成员簇，口径与 D3 一致）。");
        Assert.AreEqual(1, reviews, "只有那个 3 成员家族超限（上限 1）⇒ 恰好 1 条评审请求。");
    }

    [TestMethod]
    public void CompliantOrDefaultUnlimitedCap_ShouldProduceZeroReviewRequests()
    {
        var (compliantEvaluated, compliantReviews) = SubconsciousOrchestrator.EvaluateFamilyReviews(
            Corpus(),
            FamilyPolicy(),
            PortfolioPolicy(perFamilyCap: 3));
        var (unlimitedEvaluated, unlimitedReviews) = SubconsciousOrchestrator.EvaluateFamilyReviews(
            Corpus(),
            FamilyPolicy(),
            PortfolioPolicy(perFamilyCap: null));

        Assert.AreEqual(2, compliantEvaluated);
        Assert.AreEqual(0, compliantReviews, "恰等于上限不算超限（判据是 >）。");
        Assert.AreEqual(2, unlimitedEvaluated);
        Assert.AreEqual(0, unlimitedReviews, "不设限 ⇒ 零评审（不得把 null 读成上限 0）。");
    }

    [TestMethod]
    public void InputOrder_ShouldNotChangeTheOutcome()
    {
        // 家族键必须与输入顺序无关，否则「上限是否生效」会取决于技能恰好排在第几行。
        var forward = SubconsciousOrchestrator.EvaluateFamilyReviews(
            Corpus(),
            FamilyPolicy(),
            PortfolioPolicy(perFamilyCap: 1));
        var reversed = SubconsciousOrchestrator.EvaluateFamilyReviews(
            Corpus().Reverse().ToList(),
            FamilyPolicy(),
            PortfolioPolicy(perFamilyCap: 1));

        Assert.AreEqual(forward, reversed);
    }

    // ───────────── 报告字段默认值（空生产者路径逐字段零回归） ─────────────

    [TestMethod]
    public void CurationReport_FamilyReviewCount_ShouldDefaultToZero()
    {
        Assert.AreEqual(
            0,
            new SkillCurationReport().FamilyReviewCount,
            "D7：字段必须默认为 0，否则既有构造点会拿到一个幽灵计数。");
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>
    /// 与 D3 判据用例同构的语料：3 条近名技能（必同族）+ 1 条孤立技能。
    /// 复用同一形状是为了让「接线层」与「判据层」的家族数口径可以直接对照。
    /// </summary>
    private static IReadOnlyList<AgentSkillEvolutionDocument> Corpus() =>
    [
        Skill("skill-a", "weekly report builder"),
        Skill("skill-b", "weekly report builder tool"),
        Skill("skill-c", "weekly report builder service"),
        Skill("skill-z", "unrelated thing"),
    ];

    private static AgentSkillEvolutionDocument Skill(string id, string name) => new()
    {
        SkillId = id,
        Name = name,
        Version = "1.0.0",
        Enabled = true,
        Markdown = $"---\nname: {name}\nversion: 1.0.0\n---\nbody",
    };

    /// <summary>家族划分策略（阈值取只读报告的第一档 0.25，仅为让用例可复现；产品内不得有默认策略）。</summary>
    private static SkillFamilyPolicy FamilyPolicy() => SkillFamilyPolicy.Create(
        policyId: "skill-family/test",
        version: 1,
        nameTokenJaccardThreshold: 0.25,
        minTokenLength: 2,
        nameSeparators: SkillNameTokenization.StandardSeparators);

    private static SkillPortfolioPolicy PortfolioPolicy(int? perFamilyCap)
        => SkillPortfolioPolicy.Create("skill-portfolio/test", 1, 100, 50, perFamilyCap, 0.05, 90);
}
