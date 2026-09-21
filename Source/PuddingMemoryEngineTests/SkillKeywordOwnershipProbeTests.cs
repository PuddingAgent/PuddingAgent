using System.Reflection;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// SkillKeywordOwnershipProbe 契约用例（RSI-G4 交付物 D4）。
/// <para>
/// 探针的全部价值在于**它摆出来的事实**：每个关键词的**全部**竞争者、共享关键词数、被挤掉的注入机会数。
/// 因此每条用例都必须能在「只留第一个竞争者 / 过滤掉共享关键词 / 把 Σ(count−1) 写成 Σcount」这类
/// 变异下变红 —— 否则"事实已经暴露"就只是一句声明。
/// </para>
/// <para>
/// 真实索引上的逐数复核（G1 报告的 165 / 1735）不在这里：那是 D4c 的 env 门控只读可行性探针。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillKeywordOwnershipProbeTests
{
    // ───────────────────────── 计数口径：全部启用技能（⛔ 不是评审窗口） ─────────────────────────

    [TestMethod]
    public void Analyze_ShouldCountSlotsAcrossEnabledSkills()
    {
        var report = Fixture();

        Assert.AreEqual(3, report.EnabledSkillCount, "分母必须是全部启用技能。");
        Assert.AreEqual(8, report.SlotCount, "槽位 = Σ(每技能关键词集合大小) = 3+3+2。");
        Assert.AreEqual(6, report.DistinctKeywordCount, "去重后：alpha/beta/s1/s2/s3/shared。");
    }

    [TestMethod]
    public void Analyze_ShouldExcludeDisabledSkillsEntirely()
    {
        var report = Fixture();

        // s4 是禁用技能且声明了 shared / gamma：它既不得贡献槽位，也不得成为竞争者。
        Assert.IsFalse(
            report.Ownerships.Any(o => string.Equals(o.Keyword, "gamma", StringComparison.Ordinal)),
            "禁用技能的关键词不得进入事实集。");
        var shared = Single(report, "shared");
        CollectionAssert.AreEqual(new[] { "s1", "s2", "s3" }, shared.DeclaringSkillIds.ToArray());
    }

    // ───────────────────────── 交付物本体：每个关键词的全部竞争者 ─────────────────────────

    [TestMethod]
    public void Analyze_ShouldListEveryCompetitor_NotJustTheFirst()
    {
        var report = Fixture();

        var shared = Single(report, "shared");
        CollectionAssert.AreEqual(
            new[] { "s1", "s2", "s3" },
            shared.DeclaringSkillIds.ToArray(),
            "必须列出**全部**声明者；只留第一个就等于把冲突重新藏起来。");
        Assert.AreEqual(3, shared.DeclaredCount);
        Assert.IsTrue(shared.IsShared);
        Assert.AreEqual(2, shared.DisplacedCount, "被挤掉的次数 = 声明数 − 1。");
    }

    [TestMethod]
    public void Analyze_ShouldReportSingletonsToo()
    {
        var report = Fixture();

        var alpha = Single(report, "alpha");
        CollectionAssert.AreEqual(new[] { "s1" }, alpha.DeclaringSkillIds.ToArray());
        Assert.AreEqual(1, alpha.DeclaredCount);
        Assert.IsFalse(alpha.IsShared);
        Assert.AreEqual(0, alpha.DisplacedCount, "单声明者不产生被挤掉的机会。");
    }

    [TestMethod]
    public void Analyze_ShouldReportSharedCountAndDisplacedOpportunities()
    {
        var report = Fixture();

        Assert.AreEqual(1, report.SharedKeywordCount, "只有 shared 被 ≥2 个启用技能声明。");
        Assert.AreEqual(2, report.DisplacedInjectionCount, "Σ(声明数−1)：shared 一项 = 2。");
    }

    [TestMethod]
    public void Analyze_ShouldKeepTotalsConsistentWithTheExposedOwnerships()
    {
        var report = Fixture();

        // 总量必须由明细推导：任何"总量另算一遍"的实现都可能与摆出来的事实分叉，而报告看起来仍自洽。
        Assert.AreEqual(report.Ownerships.Count, report.DistinctKeywordCount);
        Assert.AreEqual(report.Ownerships.Sum(o => o.DeclaredCount), report.SlotCount);
        Assert.AreEqual(report.Ownerships.Sum(o => o.DisplacedCount), report.DisplacedInjectionCount);
        Assert.AreEqual(report.Ownerships.Count(o => o.IsShared), report.SharedKeywordCount);
    }

    // ───────────────────────── 确定性（与输入顺序无关） ─────────────────────────

    [TestMethod]
    public void Analyze_ShouldReturnKeywordsInOrdinalOrder()
    {
        var reversed = SkillKeywordOwnershipProbe.Analyze(
        [
            Subject("s3", "shared"),
            Subject("s2", "shared", "beta"),
            Subject("s1", "alpha", "shared"),
        ]);

        CollectionAssert.AreEqual(
            new[] { "alpha", "beta", "s1", "s2", "s3", "shared" },
            reversed.Ownerships.Select(o => o.Keyword).ToArray());
    }

    [TestMethod]
    public void Analyze_ShouldBeOrderInsensitiveAndRepeatable()
    {
        var forwards = Fixture();
        var backwards = SkillKeywordOwnershipProbe.Analyze(
        [
            Subject("s3", "shared"),
            Subject("s2", "shared", "beta"),
            Subject("s1", "alpha", "shared"),
        ]);

        // 依赖 record 的内容相等（集合字段已显式覆写）——否则这条断言会因引用相等而永远失败/永远通过。
        Assert.AreEqual(forwards, backwards, "被挤掉的次数是确定值，与输入顺序无关（G1 亦如此声明）。");
        Assert.AreEqual(forwards, Fixture(), "同一输入重复调用必须给出同一事实。");
    }

    [TestMethod]
    public void Analyze_ShouldGroupKeywordSpellingsCaseInsensitively()
    {
        var report = SkillKeywordOwnershipProbe.Analyze(
        [
            Subject("s1", "Shared"),
            Subject("s2", "shared"),
        ]);

        // 既有 map 是 OrdinalIgnoreCase：两种写法互相挤占。按序数敏感分组会把它们算成两个 ⇒ 归属无声变松。
        Assert.AreEqual(3, report.DistinctKeywordCount, "去重后 = shared + 两个 skillId（skillId 本身也是关键词）。");
        var shared = Single(report, "shared");
        CollectionAssert.AreEqual(new[] { "s1", "s2" }, shared.DeclaringSkillIds.ToArray());
        Assert.AreEqual(1, report.SharedKeywordCount);
        Assert.AreEqual(1, report.DisplacedInjectionCount);
    }

    // ───────────────────────── 失败即抛（⛔ 不用跳过/默认值掩盖） ─────────────────────────

    [TestMethod]
    public void Analyze_ShouldThrow_WhenSubjectsIsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SkillKeywordOwnershipProbe.Analyze(null!));
    }

    [TestMethod]
    public void Analyze_ShouldThrow_WhenAnySubjectIsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => SkillKeywordOwnershipProbe.Analyze([Subject("s1", "alpha"), null!]));
    }

    [TestMethod]
    public void Analyze_ShouldThrow_WhenAnEnabledSkillAppearsTwice()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SkillKeywordOwnershipProbe.Analyze([Subject("dup", "alpha"), Subject("dup", "beta")]));

        // 禁用技能不进入统计 ⇒ 重复出现不影响事实集，不得因此抛错。
        var report = SkillKeywordOwnershipProbe.Analyze(
            [Disabled("dup", "alpha"), Disabled("dup", "beta")]);
        Assert.AreEqual(0, report.EnabledSkillCount);
        Assert.IsEmpty(report.Ownerships);
    }

    // ───────────────────────── 结构闸门：零写盘、无可执行面 ─────────────────────────

    [TestMethod]
    public void ProbeAndResults_ShouldExposeNoWriteOrExecutableSurface()
    {
        var probe = typeof(SkillKeywordOwnershipProbe);

        Assert.IsTrue(probe.IsAbstract && probe.IsSealed, "探针必须是静态类（无可实例化状态）。");
        CollectionAssert.AreEqual(
            new[] { "Analyze" },
            probe.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray(),
            "探针不得长出第二个公开入口（尤其不得出现删除/禁用/改关键词之类的可执行面）。");

        CollectionAssert.AreEqual(
            new[] { "Enabled", "Keywords", "Name", "SkillId", "Tags" },
            typeof(SkillKeywordSubject).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        CollectionAssert.AreEqual(
            new[] { "DeclaredCount", "DeclaringSkillIds", "DisplacedCount", "IsShared", "Keyword" },
            typeof(SkillKeywordOwnership).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            "归属事实里不得出现『胜者/实际命中者』字段：真实顺序只有运行时索引知道，近似值不是事实。");

        CollectionAssert.AreEqual(
            new[] { "DisplacedInjectionCount", "DistinctKeywordCount", "EnabledSkillCount", "Ownerships", "SharedKeywordCount", "SlotCount" },
            typeof(SkillKeywordOwnershipReport).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ───────────────────────── helpers ─────────────────────────

    private static SkillKeywordSubject Subject(string skillId, params string[] keywords)
        => new() { SkillId = skillId, Keywords = keywords };

    private static SkillKeywordSubject Disabled(string skillId, params string[] keywords)
        => new() { SkillId = skillId, Keywords = keywords, Enabled = false };

    private static SkillKeywordOwnershipReport Fixture()
        => SkillKeywordOwnershipProbe.Analyze(
        [
            Subject("s1", "alpha", "shared"),
            Subject("s2", "shared", "beta"),
            Subject("s3", "shared"),
            Disabled("s4", "shared", "gamma"),
        ]);

    private static SkillKeywordOwnership Single(SkillKeywordOwnershipReport report, string keyword)
        => report.Ownerships.Single(o => string.Equals(o.Keyword, keyword, StringComparison.Ordinal));
}
