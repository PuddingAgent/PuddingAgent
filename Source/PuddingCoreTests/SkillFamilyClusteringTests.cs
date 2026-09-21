using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Skills.Family;

namespace PuddingCoreTests;

/// <summary>
/// RSI-G4 交付物 D1 的契约层用例：家族策略 + 名称分词口径 + 确定性家族聚类。
/// <para>
/// 纪律：**每条不变式都要有"能变红"的用例**，且负例必须断言**具体事实**（集合内容 / 家族键 / 成员序列），
/// 只断言"没崩 / 返回了非空"不算。本文件覆盖任务书 §5 的 I1（聚类确定性）与 I4（阈值来自策略对象）。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillFamilyClusteringTests
{
    // ── 名称分词口径 ────────────────────────────────────────────────────────

    [TestMethod]
    public void Tokenize_ShouldLowercaseTrimAndDropShortTokens()
    {
        var tokens = SkillNameTokenization.Tokenize("Agent Health | Check", Policy());

        CollectionAssert.AreEquivalent(
            new[] { "agent", "health", "check" }, tokens.ToList(),
            "分词必须小写化、按分隔符切分并丢弃空白项。");
    }

    [TestMethod]
    public void Tokenize_ShouldDropTokensShorterThanPolicyMinimum()
    {
        var tokens = SkillNameTokenization.Tokenize("a q b", Policy(minTokenLength: 2));

        Assert.AreEqual(0, tokens.Count, "单字符分词几乎必然制造假重叠，必须被长度下限丢弃。");
    }

    [TestMethod]
    public void Tokenize_ShouldReturnEmptySet_ForNullOrWhitespaceName()
    {
        Assert.AreEqual(0, SkillNameTokenization.Tokenize(null, Policy()).Count);
        Assert.AreEqual(0, SkillNameTokenization.Tokenize("   ", Policy()).Count);
    }

    [TestMethod]
    public void Tokenize_ShouldTakeSeparatorsFromPolicy_NotFromHardcodedSet()
    {
        // 分隔符是**策略事实**：把分隔符换成 ';' 后，空格不再是分隔符 ⇒ "Pha Qx" 必须是一个 token。
        var tokens = SkillNameTokenization.Tokenize("Al;Pha Qx", Policy(separators: [';']));

        CollectionAssert.AreEquivalent(new[] { "al", "pha qx" }, tokens.ToList());
    }

    // ── Jaccard ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Jaccard_ShouldReturnIntersectionOverUnion()
    {
        var value = SkillNameTokenization.Jaccard(
            new HashSet<string>(["a", "b"], StringComparer.Ordinal),
            new HashSet<string>(["b", "c"], StringComparer.Ordinal));

        Assert.AreEqual(1.0 / 3.0, value, 1e-9, "交集 1 / 并集 3。");
    }

    [TestMethod]
    public void Jaccard_ShouldBeZeroWheneverEitherSideIsEmpty()
    {
        var nonEmpty = new HashSet<string>(["a"], StringComparer.Ordinal);
        var empty = new HashSet<string>(StringComparer.Ordinal);

        Assert.AreEqual(0.0, SkillNameTokenization.Jaccard(empty, nonEmpty), 1e-9);
        Assert.AreEqual(0.0, SkillNameTokenization.Jaccard(nonEmpty, empty), 1e-9);
        Assert.AreEqual(0.0, SkillNameTokenization.Jaccard(empty, empty), 1e-9,
            "两个空集取 0 而非 1 —— 取 1 会让所有解析不出 token 的技能互相同族。");
        Assert.AreEqual(0.0, SkillNameTokenization.Jaccard(null, nonEmpty), 1e-9);
    }

    // ── 聚类确定性（I1）─────────────────────────────────────────────────────

    [TestMethod]
    public void Cluster_ShouldBeIndependentOfInputOrder()
    {
        var subjects = new[]
        {
            Subject("s-1", "alpha beta"),
            Subject("s-2", "alpha gamma"),
            Subject("s-3", "delta epsilon"),
        };

        var forward = SkillFamilyClusterer.Cluster(subjects, Policy(threshold: 0.3));
        var reversed = SkillFamilyClusterer.Cluster(subjects.Reverse().ToArray(), Policy(threshold: 0.3));

        Assert.AreEqual(forward.Count, reversed.Count, "输入顺序不得改变家族数量。");
        for (var i = 0; i < forward.Count; i++)
        {
            Assert.AreEqual(forward[i], reversed[i], $"第 {i} 个家族必须逐字段相同（含家族键与成员序列）。");
        }
    }

    [TestMethod]
    public void Cluster_ShouldKeepIsolatedSubjects_AsSingleMemberFamilies()
    {
        var clusters = SkillFamilyClusterer.Cluster(
            [Subject("s-1", "alpha beta"), Subject("s-2", "gamma delta"), Subject("s-3", "epsilon zeta")],
            Policy(threshold: 0.9));

        Assert.AreEqual(3, clusters.Count, "孤立技能必须保留为单成员家族，否则'未归族'这一事实会消失。");
        CollectionAssert.AreEqual(new[] { 1, 1, 1 }, clusters.Select(c => c.Size).ToArray());
    }

    [TestMethod]
    public void Cluster_ShouldUseOrdinalMinimumMember_AsFamilyKey()
    {
        var clusters = SkillFamilyClusterer.Cluster(
            [Subject("z-1", "alpha beta"), Subject("a-9", "alpha beta")],
            Policy(threshold: 0.3));

        Assert.AreEqual(1, clusters.Count);
        Assert.AreEqual("a-9", clusters[0].FamilyKey, "家族键必须是与合并顺序无关的最小成员 id。");
        CollectionAssert.AreEqual(new[] { "a-9", "z-1" }, clusters[0].Members.ToArray());
    }

    [TestMethod]
    public void Cluster_ShouldDeduplicateSkillIds_IgnoringCase()
    {
        var clusters = SkillFamilyClusterer.Cluster(
            [Subject("S-1", "alpha beta"), Subject("s-1", "alpha beta")],
            Policy(threshold: 0.3));

        Assert.AreEqual(1, clusters.Count, "同一技能的大小写变体不得变成两个家族成员。");
        Assert.AreEqual(1, clusters[0].Size);
    }

    [TestMethod]
    public void ClusterEquality_ShouldCompareMembersByContent_NotByReference()
    {
        var subjects = new[] { Subject("s-1", "alpha beta"), Subject("s-2", "alpha gamma") };

        var first = SkillFamilyClusterer.Cluster(subjects, Policy(threshold: 0.3))[0];
        var second = SkillFamilyClusterer.Cluster(subjects.Reverse().ToArray(), Policy(threshold: 0.3))[0];

        Assert.AreEqual(first, second,
            "簇的相等性必须按成员内容比较；按引用比较会让'两次划分一致'这类断言静默失效。");
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
    }

    // ── 并查集传递闭包（被显式钉住的语义）──────────────────────────────────

    [TestMethod]
    public void Cluster_ShouldGroupTransitively_EvenWhenEndsAreDissimilar()
    {
        // A~B、B~C 相似度均为 1/3 ≥ 0.3，而 A 与 C 的交集为 0 ⇒ 仍必须合并为一个家族。
        var clusters = SkillFamilyClusterer.Cluster(
            [Subject("a-1", "pa qb"), Subject("a-2", "qb rc"), Subject("a-3", "rc sd")],
            Policy(threshold: 0.3));

        Assert.AreEqual(1, clusters.Count, "链条式相似必须传递合并（Jaccard 本身不满足传递性）。");
        CollectionAssert.AreEqual(new[] { "a-1", "a-2", "a-3" }, clusters[0].Members.ToArray());
    }

    // ── 阈值来自策略对象（I4）──────────────────────────────────────────────

    [TestMethod]
    public void Cluster_ShouldTakeThresholdFromPolicy()
    {
        var subjects = new[] { Subject("s-1", "alpha beta"), Subject("s-2", "alpha gamma") };

        var belowHard = SkillFamilyClusterer.Cluster(subjects, Policy(threshold: 0.34));
        var aboveSoft = SkillFamilyClusterer.Cluster(subjects, Policy(threshold: 0.30));

        Assert.AreEqual(2, belowHard.Count, "相似度 1/3 < 0.34 ⇒ 两个单成员家族。");
        Assert.AreEqual(1, aboveSoft.Count, "相似度 1/3 ≥ 0.30 ⇒ 合并为一个家族。");
        Assert.AreEqual(2, aboveSoft[0].Size);
    }

    [TestMethod]
    public void Cluster_ShouldMerge_WhenSimilarityEqualsThresholdExactly()
    {
        // 边界语义（在变异取红时补上）：相似度**恰好等于**阈值必须合并 —— 判据是 ≥，不是 >。
        // 为何必须有这条：把实现里的 ≥ 改成 > 后**没有任何用例变红** ⇒ 该边界此前无人钉住。
        var exact = SkillNameTokenization.Jaccard(
            SkillNameTokenization.Tokenize("alpha beta", Policy()),
            SkillNameTokenization.Tokenize("alpha gamma", Policy()));

        var clusters = SkillFamilyClusterer.Cluster(
            [Subject("s-1", "alpha beta"), Subject("s-2", "alpha gamma")],
            Policy(threshold: exact));

        Assert.AreEqual(1, clusters.Count, $"相似度 {exact} 恰好等于阈值时必须合并（判据是 ≥，不是 >）。");
        Assert.AreEqual(2, clusters[0].Size);
    }

    [TestMethod]
    public void Cluster_ShouldReturnEmpty_ForNoSubjects()
    {
        Assert.AreEqual(0, SkillFamilyClusterer.Cluster([], Policy()).Count);
    }

    // ── 构造期拒绝非法配置 ──────────────────────────────────────────────────

    [TestMethod]
    public void PolicyCreate_ShouldRejectThresholdOutsideUnitInterval()
    {
        foreach (var threshold in new[] { 0.0, -0.1, 1.5, double.NaN, double.PositiveInfinity })
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => Policy(threshold: threshold),
                $"阈值 {threshold} 必须被构造期拒绝（0 ⇒ 全体同族；>1 ⇒ 判据恒不触发）。");
        }
    }

    [TestMethod]
    public void PolicyCreate_ShouldRejectInvalidMinTokenLengthSeparatorsAndId()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Policy(minTokenLength: 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => Policy(separators: []));
        Assert.ThrowsExactly<InvalidOperationException>(() => Policy(separators: [' ', ' ']));
        Assert.ThrowsExactly<InvalidOperationException>(() => Policy(separators: ['\0']));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SkillFamilyPolicy.Create("  ", 1, 0.3, 2, SkillNameTokenization.StandardSeparators));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SkillFamilyPolicy.Create("skill-family/test", 0, 0.3, 2, SkillNameTokenization.StandardSeparators));
    }

    [TestMethod]
    public void Cluster_ShouldRejectInvalidPolicy_EvenWhenCreateWasBypassed()
    {
        // record 的 required 属性允许绕过 Create 直接构造 ⇒ 判定入口必须自己校验。
        var raw = new SkillFamilyPolicy
        {
            PolicyId = "skill-family/bypass",
            Version = 1,
            NameTokenJaccardThreshold = 0,
            MinTokenLength = 2,
            NameSeparators = [' '],
        };

        Assert.ThrowsExactly<InvalidOperationException>(
            () => SkillFamilyClusterer.Cluster([Subject("s-1", "alpha beta")], raw));
    }

    [TestMethod]
    public void SubjectCreate_ShouldRejectBlankSkillId_AndNormalizeNullName()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SkillFamilySubject.Create("  ", "alpha beta"));

        var subject = SkillFamilySubject.Create("s-1", null);
        Assert.AreEqual(string.Empty, subject.Name);
    }

    // ── 快照 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ToApplied_ShouldCarryEveryConsumedKnob()
    {
        var applied = Policy(threshold: 0.42, minTokenLength: 3, separators: [' ']).ToApplied();

        Assert.AreEqual("skill-family/test", applied.PolicyId);
        Assert.AreEqual(1, applied.Version);
        Assert.AreEqual(0.42, applied.NameTokenJaccardThreshold, 1e-9);
        Assert.AreEqual(3, applied.MinTokenLength);
        CollectionAssert.AreEqual(new[] { ' ' }, applied.NameSeparators.ToArray());
    }

    private static SkillFamilyPolicy Policy(
        double threshold = 0.3,
        int minTokenLength = 2,
        IReadOnlyList<char>? separators = null)
        => SkillFamilyPolicy.Create(
            policyId: "skill-family/test",
            version: 1,
            nameTokenJaccardThreshold: threshold,
            minTokenLength: minTokenLength,
            nameSeparators: separators ?? SkillNameTokenization.StandardSeparators);

    private static SkillFamilySubject Subject(string skillId, string name)
        => SkillFamilySubject.Create(skillId, name);
}
