using System.Reflection;
using PuddingCode.Skills.Family;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// RSI-G4 交付物 **D6a**：合并**四条件**判据契约用例。
/// <para>
/// 权威出处：任务书 §2.5（四条件逐条实现且**逐条可单独取红**；判据是**只读**的，⛔ 不得执行任何合并动作；
/// ⛔ 判据**不是**既有 <c>IsDeterministicallyEligible</c>，也不得替换/放宽它）、
/// §2.6（阈值全部来自策略对象，⛔ 不得写死字面量）、不变式 **I6 / I8**。
/// </para>
/// <para>
/// 纪律：**只写用例不证明它会红 ⇒ 不算完成**。每条断言都注明"什么变异会让它变红"。
/// D6b（只读可行性探针：在 §3-F 的 10 个既有近重复簇上统计"过四条件"与"再叠加既有闸门"的对数）另行交付。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillMergeEligibilityJudgeTests
{
    /// <summary>占位相似度：只认逐字相同。既有 token-Jaccard 与它在"部分重叠"文本上结论相反 ⇒ 可区分"用的是哪一份实现"。</summary>
    private static readonly Func<string, string, double> MarkerSimilarity =
        static (left, right) => string.Equals(left, right, StringComparison.Ordinal) ? 1.0 : 0.0;

    // ───────────── 四条件：全满足 ⇒ 合格 ─────────────

    [TestMethod]
    public void Apply_WithAllFourConditions_ShouldBeEligible()
    {
        var report = new SkillMergeEligibilityJudge(MarkerSimilarity).Apply(
            [Pair(
                Evidence("skill-a", keywords: ["shared-keyword", "only-a"]),
                Evidence("skill-b", keywords: ["shared-keyword", "only-b"]))],
            Policy());

        var verdict = report.Verdicts.Single();

        Assert.IsTrue(verdict.IsEligible, "四条件全满足时必须判为合格。");
        Assert.HasCount(0, verdict.FailedConditions, "合格配对的失败条件列表必须为空。");
        Assert.AreEqual(1, report.EligiblePairCount);
        Assert.AreEqual(1.0, verdict.ProceduralTextSimilarity, "裁决必须保留实际算得的相似度（供事后解释）。");
    }

    // ───────────── 四条件：逐条可单独取红（每条一个负例，且失败条件必须是"唯一的那一条"） ─────────────

    [TestMethod]
    public void Apply_WithADifferentFamily_ShouldFailOnlyTheFamilyCondition()
    {
        var verdict = Single(Pair(
            Evidence("skill-a", familyKey: "family-alpha"),
            Evidence("skill-b", familyKey: "family-beta")));

        CollectionAssert.AreEqual(
            new[] { SkillMergeCondition.SameFamily },
            verdict.FailedConditions.ToList(),
            "把同族条件当成恒真（或整条去掉）时本用例必红：失败条件里不再出现 SameFamily。");
        Assert.IsFalse(verdict.IsEligible, "异族配对必须判为不合格。");
    }

    [TestMethod]
    public void Apply_WithoutKeywordOverlap_ShouldFailOnlyTheKeywordCondition()
    {
        var verdict = Single(Pair(
            Evidence("skill-a", keywords: ["only-a"]),
            Evidence("skill-b", keywords: ["only-b"])));

        CollectionAssert.AreEqual(
            new[] { SkillMergeCondition.KeywordsOverlap },
            verdict.FailedConditions.ToList(),
            "把关键词重叠条件删掉（或让交集判定恒真）时本用例必红。");
        Assert.IsFalse(verdict.IsEligible);
    }

    [TestMethod]
    public void Apply_WithDifferentProceduralText_ShouldFailOnlyTheTextCondition()
    {
        var verdict = Single(Pair(
            Evidence("skill-a", proceduralText: "procedure-one"),
            Evidence("skill-b", proceduralText: "procedure-two")));

        CollectionAssert.AreEqual(
            new[] { SkillMergeCondition.ProceduralTextSimilarity },
            verdict.FailedConditions.ToList(),
            "把文本阈值比较删掉（或把 ≥ 写成恒真）时本用例必红。");
        Assert.AreEqual(0.0, verdict.ProceduralTextSimilarity, "相似度必须如实记录（占位实现判为完全不相似）。");
    }

    [TestMethod]
    public void Apply_WithoutSharedEvidence_ShouldFailOnlyTheEvidenceCondition()
    {
        var verdict = Single(Pair(
            Evidence("skill-a", evidenceIds: ["turn-1"]),
            Evidence("skill-b", evidenceIds: ["turn-2"])));

        CollectionAssert.AreEqual(
            new[] { SkillMergeCondition.MergeableEvidence },
            verdict.FailedConditions.ToList(),
            "把证据可归并条件删掉（或让共享判定恒真）时本用例必红。");
        Assert.IsFalse(verdict.IsEligible);
    }

    // ───────────── 阈值语义：≥ 边界、且必须来自策略对象 ─────────────

    [TestMethod]
    public void Apply_AtExactlyTheThreshold_ShouldBeEligible()
    {
        var pair = Pair(
            Evidence("skill-a", proceduralText: "left-body"),
            Evidence("skill-b", proceduralText: "right-body"));
        var judge = new SkillMergeEligibilityJudge(ConstantSimilarity(0.5));

        var atThreshold = judge.Apply([pair], Policy(threshold: 0.5)).Verdicts.Single();
        var justAbove = judge.Apply([pair], Policy(threshold: 0.5000001)).Verdicts.Single();

        Assert.IsTrue(atThreshold.IsEligible, "阈值语义是『≥』：恰好等于阈值必须通过。");
        CollectionAssert.AreEqual(
            new[] { SkillMergeCondition.ProceduralTextSimilarity },
            justAbove.FailedConditions.ToList(),
            "把『≥』写成『>』时本用例必红：恰好等于阈值会被判为不合格。");
    }

    [TestMethod]
    public void Apply_ShouldUseThePolicyThreshold_NotAHardCodedValue()
    {
        var pair = Pair(
            Evidence("skill-a", proceduralText: "left-body"),
            Evidence("skill-b", proceduralText: "right-body"));
        var judge = new SkillMergeEligibilityJudge(ConstantSimilarity(0.5));

        var loose = judge.Apply([pair], Policy(threshold: 0.3, version: 1)).Verdicts.Single();
        var strict = judge.Apply([pair], Policy(threshold: 0.9, version: 2)).Verdicts.Single();

        Assert.IsTrue(loose.IsEligible, "同一相似度在宽松阈值下必须通过。");
        Assert.IsFalse(strict.IsEligible, "把阈值写死成字面量（而不是读策略）时本用例必红。");
        Assert.AreEqual("skill-merge/test@1", judge.Apply([pair], Policy(0.3, version: 1)).PolicyRef);
        Assert.AreEqual("skill-merge/test@2", judge.Apply([pair], Policy(0.9, version: 2)).PolicyRef,
            "结论必须自证用的是哪一版阈值。");
    }

    // ───────────── 相似度实现必须注入（⛔ 判据不得自带第二套） ─────────────

    [TestMethod]
    public void Apply_ShouldUseTheInjectedSimilaritySource_NotAPrivateCopy()
    {
        // 既有 token-Jaccard（词元集合 Jaccard）会把这两段判为 3/4 = 0.75（≥ 0.5 ⇒ 通过）；
        // 注入的占位实现只认逐字相同 ⇒ 必须判为不通过。两者结论相反，故本用例可区分"用的是哪一份实现"。
        var verdict = Single(Pair(
            Evidence("skill-a", proceduralText: "alpha beta gamma"),
            Evidence("skill-b", proceduralText: "alpha beta gamma delta")));

        Assert.IsFalse(
            verdict.IsEligible,
            "判据自带第二套相似度实现（而不是使用注入的那一份）时本用例必红：它会自己算出 0.75 并判为合格。");
        CollectionAssert.AreEqual(
            new[] { SkillMergeCondition.ProceduralTextSimilarity },
            verdict.FailedConditions.ToList());
        Assert.AreEqual(0.0, verdict.ProceduralTextSimilarity, "保留的相似度必须是注入实现算出的值。");
    }

    // ───────────── 明细与总量：总量由明细推导、不截断、顺序无关 ─────────────

    [TestMethod]
    public void Report_ShouldDeriveTotalsAndPolicyRefFromTheVerdicts()
    {
        var report = new SkillMergeEligibilityJudge(MarkerSimilarity).Apply(
            [
                Pair(Evidence("skill-a"), Evidence("skill-b")),
                Pair(Evidence("skill-c", familyKey: "family-beta"), Evidence("skill-d")),
                Pair(Evidence("skill-e", evidenceIds: ["turn-9"]), Evidence("skill-f")),
            ],
            Policy(policyId: "skill-merge/test", version: 3, threshold: 0.5));

        Assert.AreEqual(3, report.EvaluatedPairCount);
        Assert.AreEqual(1, report.EligiblePairCount);
        Assert.AreEqual(
            report.Verdicts.Count(static verdict => verdict.IsEligible),
            report.EligiblePairCount,
            "总量必须由明细推导；另行累加（两处各算一遍）时本用例必红。");
        Assert.AreEqual("skill-merge/test@3", report.PolicyRef);
    }

    [TestMethod]
    public void Apply_ShouldKeepEveryPair_NotOnlyTheEligibleOnes()
    {
        var pairs = new List<SkillMergePair>();
        for (var index = 0; index < 11; index++)
        {
            pairs.Add(Pair(
                Evidence($"skill-fam-a-{index:D2}", familyKey: "family-alpha"),
                Evidence($"skill-fam-b-{index:D2}", familyKey: "family-beta")));
        }

        pairs.Add(Pair(Evidence("skill-ok-1"), Evidence("skill-ok-2")));

        var report = new SkillMergeEligibilityJudge(MarkerSimilarity).Apply(pairs, Policy());

        Assert.AreEqual(12, report.EvaluatedPairCount, "评估总数必须等于输入配对数（⛔ 不做 Top-N 截断）。");
        Assert.HasCount(
            12,
            report.Verdicts,
            "对明细做 Top-N 截断、或只保留合格配对时本用例必红。");
        Assert.AreEqual(1, report.EligiblePairCount);
        Assert.IsTrue(report.Verdicts.Any(static verdict => !verdict.IsEligible), "不合格配对同样必须留在明细里。");
    }

    [TestMethod]
    public void Apply_ShouldBeOrderInsensitiveAndRepeatable()
    {
        var judge = new SkillMergeEligibilityJudge(MarkerSimilarity);
        var pairs = new List<SkillMergePair>
        {
            Pair(Evidence("skill-c", evidenceIds: ["turn-3"]), Evidence("skill-d")),
            Pair(Evidence("skill-a"), Evidence("skill-b")),
            Pair(Evidence("skill-e", familyKey: "family-beta"), Evidence("skill-f")),
        };

        var forward = Signature(judge.Apply(pairs, Policy()));
        var reversed = Signature(judge.Apply(pairs.AsEnumerable().Reverse().ToList(), Policy()));
        var repeated = Signature(judge.Apply(pairs, Policy()));

        CollectionAssert.AreEqual(
            forward,
            reversed,
            "明细顺序取决于输入顺序时本用例必红（同一输入必须得到同一裁决）。");
        CollectionAssert.AreEqual(forward, repeated, "同一输入重复判定必须得到同一结果。");
    }

    // ───────────── fail-closed：畸形输入一律拒绝（⛔ 不静默跳过） ─────────────

    [TestMethod]
    public void Apply_ShouldRejectNullInputsAndInvalidPolicy()
    {
        var judge = new SkillMergeEligibilityJudge(MarkerSimilarity);
        var pair = Pair(Evidence("skill-a"), Evidence("skill-b"));

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SkillMergeEligibilityJudge(null!),
            "相似度实现为空时必须构造期拒绝。");
        Assert.ThrowsExactly<ArgumentNullException>(() => judge.Apply(null!, Policy()));
        Assert.ThrowsExactly<ArgumentNullException>(() => judge.Apply([null!], Policy()));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => judge.Apply([new SkillMergePair { Left = Evidence("skill-a"), Right = null! }], Policy()));

        var bypassed = new SkillMergePolicy { PolicyId = "skill-merge/test", Version = 1, MinimumProceduralTextSimilarity = 0 };
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply([pair], bypassed),
            "绕过工厂构造的非法策略必须在判定入口被拒绝（判据不信任调用方）。");
    }

    /// <summary>
    /// D6c：重叠判定必须与 D4 的归一口径用**同一个**比较器 —— D4 的产出保留原始大小写，
    /// 因此判据不得要求调用方先折叠（那是第二套口径，且实测会让真实数据整体被拒）。
    /// </summary>
    [TestMethod]
    public void Apply_ShouldMatchKeywordsCaseInsensitively_UsingTheCanonicalComparer()
    {
        var judge = new SkillMergeEligibilityJudge(MarkerSimilarity);
        var report = judge.Apply(
            [
                Pair(
                    Evidence("skill-a", keywords: ["Shared-Keyword"]),
                    Evidence("skill-b", keywords: ["shared-keyword"])),
            ],
            Policy());

        Assert.AreEqual(
            1,
            report.EligiblePairCount,
            "大小写不同但同一关键词必须视为重叠（KeywordComparer 口径）；判为不重叠就是口径分裂。");
        Assert.AreEqual(0, report.Verdicts[0].FailedConditions.Count);
    }

    [TestMethod]
    public void Apply_ShouldRejectMalformedFacts_InsteadOfSilentlySkipping()
    {
        var judge = new SkillMergeEligibilityJudge(MarkerSimilarity);
        var policy = Policy();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply([Pair(Evidence("skill-a"), Evidence("skill-a"))], policy),
            "技能出现在配对两侧（自配对）时必须拒绝。");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply(
                [
                    Pair(Evidence("skill-a"), Evidence("skill-b")),
                    Pair(Evidence("skill-b"), Evidence("skill-a")),
                ],
                policy),
            "同一配对（反向写法）被判定两次时必须拒绝：否则评估总数会虚高。");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply([Pair(Evidence(" "), Evidence("skill-b"))], policy));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply([Pair(Evidence("skill-a", familyKey: " "), Evidence("skill-b"))], policy),
            "未参与家族划分（FamilyKey 为空）的技能不得进入合并判定。");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply([Pair(Evidence("skill-a", keywords: [" "]), Evidence("skill-b"))], policy),
            "空白关键词必须拒绝：它不携带任何可比较信息，却会参与重叠判定。");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply([Pair(Evidence("skill-a", evidenceIds: [" "]), Evidence("skill-b"))], policy));
    }

    [TestMethod]
    public void Apply_ShouldRejectNonFiniteSimilarity_InsteadOfJudgingIt()
    {
        var judge = new SkillMergeEligibilityJudge(ConstantSimilarity(double.NaN));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply([Pair(Evidence("skill-a"), Evidence("skill-b"))], Policy()),
            "相似度实现返回 NaN 时必须抛错：静默判为『不相似』会把实现缺陷伪装成业务结论。");
    }

    // ───────────── 策略一等对象：非法阈值构造期拒绝、无默认值 ─────────────

    [TestMethod]
    public void Policy_ShouldRejectOutOfRangeThreshold()
    {
        foreach (var threshold in new[] { 0.0, -0.1, 1.5, double.NaN, double.PositiveInfinity })
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => SkillMergePolicy.Create("skill-merge/test", 1, threshold),
                $"阈值 {threshold} 非法（合法区间 (0, 1]，0 会让文本条件恒真、>1 会恒假）。");
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => SkillMergePolicy.Create(" ", 1, 0.5));
        Assert.ThrowsExactly<InvalidOperationException>(() => SkillMergePolicy.Create("skill-merge/test", 0, 0.5));

        var policy = SkillMergePolicy.Create("skill-merge/test", 7, 1.0);
        Assert.IsTrue(policy.IsValid);
        Assert.AreEqual(1.0, policy.ToApplied().MinimumProceduralTextSimilarity);
        Assert.AreEqual(7, policy.ToApplied().Version, "快照必须带上版本，历史结论才能自证口径。");
    }

    // ───────────── 结构性闸门：只读、唯一入口、四条件集合被钉住 ─────────────

    [TestMethod]
    public void Conditions_ShouldBeExactlyTheFourFromTheTaskBook()
    {
        var conditions = Enum.GetValues<SkillMergeCondition>();

        CollectionAssert.AreEqual(
            new[]
            {
                SkillMergeCondition.SameFamily,
                SkillMergeCondition.KeywordsOverlap,
                SkillMergeCondition.ProceduralTextSimilarity,
                SkillMergeCondition.MergeableEvidence,
            },
            conditions,
            "四条件集合/顺序是任务书 §2.5 的事实；增删或重排（例如把两条合并成一条）时本用例必红。");
    }

    [TestMethod]
    public void JudgeAndVerdicts_ShouldExposeNoWriteOrExecutableSurface()
    {
        var judgeType = typeof(SkillMergeEligibilityJudge);

        Assert.HasCount(1, judgeType.GetConstructors(), "判据只允许一个入口：注入相似度实现的构造器。");

        var publicMethods = judgeType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name)
            .ToList();
        CollectionAssert.AreEqual(
            new[] { "Apply" },
            publicMethods,
            "判据只允许一个公有实例方法（Apply）；出现任何写入/执行入口时本用例必红。");

        var fields = judgeType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .ToList();
        Assert.HasCount(1, fields, "判据的状态只能有一个字段：注入的相似度实现（⛔ 不得缓存语料、连接、日志器等）。");
        Assert.AreEqual(
            typeof(Func<string, string, double>),
            fields[0].FieldType,
            "唯一字段必须是注入的相似度实现。");

        var source = File.ReadAllText(JudgeSourcePath());
        foreach (var forbidden in new[]
                 {
                     "File.", "Directory.", "Sqlite", "HttpClient", "Process.Start", "Task.Run",
                     "UpdateAsync", "DeleteAsync", "IAgentSkillEvolutionStore", "ILogger", "Console.",
                 })
        {
            Assert.IsFalse(
                source.Contains(forbidden, StringComparison.Ordinal),
                $"合并判据是只读判据：源码不得出现 `{forbidden}`（任何 IO / 写入 / 执行面都会被本断言取红）。");
        }
    }

    // ───────────── helpers ─────────────

    private static SkillMergePolicy Policy(
        double threshold = 0.5,
        string policyId = "skill-merge/test",
        int version = 1)
        => SkillMergePolicy.Create(policyId, version, threshold);

    private static Func<string, string, double> ConstantSimilarity(double value)
        => (_, _) => value;

    private static SkillMergeEvidence Evidence(
        string skillId,
        string familyKey = "family-alpha",
        IReadOnlyList<string>? keywords = null,
        string proceduralText = "shared-procedure",
        IReadOnlyList<string>? evidenceIds = null)
        => new()
        {
            SkillId = skillId,
            FamilyKey = familyKey,
            Keywords = keywords ?? ["shared-keyword"],
            ProceduralText = proceduralText,
            EvidenceIds = evidenceIds ?? ["turn-1"],
        };

    private static SkillMergePair Pair(SkillMergeEvidence left, SkillMergeEvidence right)
        => new() { Left = left, Right = right };

    private static SkillMergeVerdict Single(SkillMergePair pair)
        => new SkillMergeEligibilityJudge(MarkerSimilarity)
            .Apply([pair], Policy())
            .Verdicts.Single();

    private static List<string> Signature(SkillMergeEligibilityReport report)
        => report.Verdicts
            .Select(static verdict =>
                $"{verdict.LeftSkillId}~{verdict.RightSkillId}~{verdict.IsEligible}~"
                + $"{string.Join('|', verdict.FailedConditions)}~{verdict.ProceduralTextSimilarity}")
            .ToList();

    private static string JudgeSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Source",
                "PuddingMemoryEngine",
                "Services",
                "SkillMergeEligibilityJudge.cs");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        Assert.Fail("找不到 SkillMergeEligibilityJudge.cs 源码路径，结构性断言无法执行。");
        return string.Empty;
    }
}
