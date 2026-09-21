using PuddingCode.Skills.Family;
using PuddingCode.Skills.Portfolio;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// G4 交付物 D3：家族内上限判据契约用例。
/// <para>
/// 纪律：每条用例对应任务书 §5 的一条不变式（I2 / I3 / I4 / I7 及其确定性延伸），
/// 且每条都必须能在实现被改坏时**变红**（只写用例不证明它会红 ⇒ 不算完成）。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillFamilyCapJudgeTests
{
    // ───────────────────────── I2 不设限必须"任何家族都不超限" ─────────────────────────

    [TestMethod]
    public void PerFamilyCapNull_ShouldProduceNoReview_ForAnyFamily()
    {
        var report = new SkillFamilyCapJudge(Policy(perFamilyCap: null))
            .Apply([Cluster("a", "a", "b", "c"), Cluster("z", "z", "y")]);

        Assert.IsEmpty(report.Reviews, "不设限 ⇒ 零评审（不得把 null 当成上限 0）。");
        Assert.IsNull(report.Cap, "报告必须如实保留 null，否则读者无法区分『没设限』与『设了限但没超』。");
        Assert.AreEqual(2, report.EvaluatedFamilyCount, "评估过的家族数必须如实报告（分母不能省）。");
    }

    [TestMethod]
    public void ZeroReviews_ShouldStillExposeWhetherTheCapWasEnforced()
    {
        // "0 条评审"有两种完全不同的含义：本片没设限 / 设了限但全部合规。
        // 把二者压成同一个 0，就等于制造一个无人会失败的幻影区间。
        var unlimited = new SkillFamilyCapJudge(Policy(perFamilyCap: null)).Apply([Cluster("a", "a", "b", "c")]);
        var compliant = new SkillFamilyCapJudge(Policy(perFamilyCap: 3)).Apply([Cluster("a", "a", "b", "c")]);

        Assert.IsEmpty(unlimited.Reviews);
        Assert.IsEmpty(compliant.Reviews);
        Assert.IsNull(unlimited.Cap);
        Assert.AreEqual(3, compliant.Cap);
    }

    // ───────────────────────── 边界：超限判据是 >，不是 >= ─────────────────────────

    [TestMethod]
    public void FamilyAtExactlyTheCap_ShouldNotBeReviewRequested()
    {
        var report = new SkillFamilyCapJudge(Policy(perFamilyCap: 3)).Apply([Cluster("a", "a", "b", "c")]);

        Assert.IsEmpty(report.Reviews, "『启用数恰好等于上限』不是超限（设计原文是 > perFamilyCap）。");
    }

    [TestMethod]
    public void FamilyOneOverTheCap_ShouldBeReviewRequested_WithEveryContractField()
    {
        var report = new SkillFamilyCapJudge(Policy(perFamilyCap: 3, policyId: "skill-portfolio/test", version: 7))
            .Apply([Cluster("a", "a", "b", "c", "d")]);

        var review = report.Reviews.Single();
        Assert.AreEqual("a", review.FamilyKey, "家族键必须原样来自簇（序数序最小成员）。");
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, review.Members.ToList());
        Assert.AreEqual(4, review.EnabledCount, "§2.3：记录必须含超限数。");
        Assert.AreEqual(3, review.Cap);
        Assert.AreEqual(1, review.OverBy);
        Assert.AreEqual("family_over_cap", review.ReasonCode, "§2.3：记录必须含 reason code。");
        Assert.AreEqual("skill-portfolio/test@7", review.PolicyRef, "§2.3：记录必须含策略 policyId@version。");
        Assert.AreEqual("skill-portfolio/test@7", report.PolicyRef);
    }

    // ───────────────────────── I4 上限来自策略对象，不是裸字面量 ─────────────────────────

    [TestMethod]
    public void Cap_ShouldComeFromPolicyInstance_NotFromHardCodedValue()
    {
        // 两个家族各 2 成员：上限 2 ⇒ 全合规；上限 1 ⇒ 两条评审。阈值若被写死，用例必红。
        var clusters = new[] { Cluster("a", "a", "b"), Cluster("z", "z", "y") };

        Assert.IsEmpty(new SkillFamilyCapJudge(Policy(perFamilyCap: 2)).Apply(clusters).Reviews);
        Assert.HasCount(2, new SkillFamilyCapJudge(Policy(perFamilyCap: 1)).Apply(clusters).Reviews);
    }

    [TestMethod]
    public void PolicyRef_ShouldCarryTheVersionActuallyUsed()
    {
        var first = new SkillFamilyCapJudge(Policy(perFamilyCap: 1, version: 1)).Apply([Cluster("a", "a", "b")]);
        var second = new SkillFamilyCapJudge(Policy(perFamilyCap: 1, version: 2)).Apply([Cluster("a", "a", "b")]);

        Assert.AreEqual("skill-portfolio/test@1", first.Reviews.Single().PolicyRef);
        Assert.AreEqual("skill-portfolio/test@2", second.Reviews.Single().PolicyRef);
    }

    // ───────────────────────── 确定性延伸：输出与输入顺序无关 ─────────────────────────

    [TestMethod]
    public void Reviews_ShouldBeOrderedByFamilyKey_RegardlessOfInputOrder()
    {
        var forward = new[] { Cluster("a", "a", "b", "c"), Cluster("z", "z", "y", "x") };
        var reversed = new[] { forward[1], forward[0] };
        var judge = new SkillFamilyCapJudge(Policy(perFamilyCap: 1));

        var first = judge.Apply(forward);
        var second = judge.Apply(reversed);

        CollectionAssert.AreEqual(new[] { "a", "z" }, first.Reviews.Select(review => review.FamilyKey).ToList());
        CollectionAssert.AreEqual(
            first.Reviews.ToList(),
            second.Reviews.ToList(),
            "同一集合的不同输入顺序必须给出同一报告（否则上限是否生效取决于索引文件里的行序）。");
    }

    [TestMethod]
    public void Apply_ShouldBeRepeatable_WithContentEquality()
    {
        var clusters = new[] { Cluster("a", "a", "b", "c") };
        var judge = new SkillFamilyCapJudge(Policy(perFamilyCap: 1));

        // 集合字段若用引用相等，这条断言会**永远失败**（而不是静默通过），故它同时钉住了
        // SkillFamilyCapReview / SkillFamilyCapReviewReport 的内容相等语义。
        Assert.AreEqual(judge.Apply(clusters), judge.Apply(clusters), "两次判定必须内容相等。");
    }

    // ───────────────────────── I3 / I7 判定器在结构上不具备写盘能力 ─────────────────────────

    [TestMethod]
    public void JudgeAndResults_ShouldExposeNoWriteOrExecutableSurface()
    {
        // ① I7 零写盘：判定器只依赖策略 —— 没有任何仓储 / IO / 写方法可注入，故"零写盘"是类型事实。
        var constructor = typeof(SkillFamilyCapJudge).GetConstructors().Single();
        CollectionAssert.AreEqual(
            new[] { typeof(SkillPortfolioPolicy) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType).ToList(),
            "判定器一旦能接收仓储/IO，『零写盘』就不再是类型事实。");

        // ② I3 超限后果只有评审请求：记录里不得长出"禁用谁 / 合并到谁"这类可执行字段。
        CollectionAssert.AreEqual(
            new[] { "Cap", "EnabledCount", "FamilyKey", "Members", "OverBy", "PolicyRef", "ReasonCode" },
            typeof(SkillFamilyCapReview).GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList(),
            "评审记录必须只是待裁决事实，⛔ 不得携带可执行语义。");

        CollectionAssert.AreEqual(
            new[] { "Cap", "EvaluatedFamilyCount", "PolicyRef", "Reviews" },
            typeof(SkillFamilyCapReviewReport).GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList());
    }

    // ───────────────────────── 入口校验：入口即校验，不信任调用方 ─────────────────────────

    [TestMethod]
    public void Apply_ShouldRejectInvalidPolicy_EvenWhenCreateWasBypassed()
    {
        var invalid = new SkillPortfolioPolicy
        {
            PolicyId = "skill-portfolio/test",
            Version = 1,
            HardCap = 4,
            SoftTarget = 9,
            PerFamilyCap = null,
            MinMarginalGain = 0.05,
            StalenessDays = 90,
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => new SkillFamilyCapJudge(invalid).Apply([Cluster("a", "a", "b")]));
    }

    [TestMethod]
    public void Apply_ShouldRejectNullInputs()
    {
        var judge = new SkillFamilyCapJudge(Policy(perFamilyCap: 1));

        Assert.ThrowsExactly<ArgumentNullException>(() => new SkillFamilyCapJudge(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => judge.Apply(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => judge.Apply([null!]));
    }

    // ───────────────────────── 与 D1 联动：真实聚类结果上的端到端判定 ─────────────────────────

    [TestMethod]
    public void OnRealClusters_ShouldReviewOnlyTheOversizedFamily()
    {
        var subjects = new[]
        {
            SkillFamilySubject.Create("skill-a", "weekly report builder"),
            SkillFamilySubject.Create("skill-b", "weekly report builder tool"),
            SkillFamilySubject.Create("skill-c", "weekly report builder service"),
            SkillFamilySubject.Create("skill-z", "unrelated thing"),
        };
        var clusters = SkillFamilyClusterer.Cluster(subjects, FamilyPolicy());

        var report = new SkillFamilyCapJudge(Policy(perFamilyCap: 2)).Apply(clusters);

        Assert.AreEqual(2, report.EvaluatedFamilyCount, "3 条同族 + 1 条孤立 ⇒ 2 个家族（含单成员簇）。");
        var review = report.Reviews.Single();
        Assert.AreEqual("skill-a", review.FamilyKey);
        Assert.AreEqual(3, review.EnabledCount);
        Assert.AreEqual(1, review.OverBy);
        CollectionAssert.AreEqual(new[] { "skill-a", "skill-b", "skill-c" }, review.Members.ToList());
    }

    // ───────────────────────── helpers ─────────────────────────

    private static SkillPortfolioPolicy Policy(
        int? perFamilyCap,
        string policyId = "skill-portfolio/test",
        int version = 1)
        => SkillPortfolioPolicy.Create(
            policyId,
            version,
            hardCap: 100,
            softTarget: 50,
            perFamilyCap,
            minMarginalGain: 0.05,
            stalenessDays: 90);

    /// <summary>家族划分策略（阈值取只读报告的第一档 0.25，仅为让端到端用例可复现）。</summary>
    private static SkillFamilyPolicy FamilyPolicy()
        => SkillFamilyPolicy.Create(
            policyId: "skill-family/test",
            version: 1,
            nameTokenJaccardThreshold: 0.25,
            minTokenLength: 1,
            nameSeparators: SkillNameTokenization.StandardSeparators);

    private static SkillFamilyCluster Cluster(string familyKey, params string[] members)
        => new() { FamilyKey = familyKey, Members = members };
}
