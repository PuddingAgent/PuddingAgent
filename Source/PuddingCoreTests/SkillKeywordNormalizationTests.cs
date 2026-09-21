using PuddingCode.Skills.Family;
using PuddingCode.Skills.Retrieval;

namespace PuddingCoreTests;

/// <summary>
/// SkillKeywordNormalization 契约用例。
/// <para>
/// 这些用例钉的不是"代码好不好看"，而是**口径本身**：G1 报告（`Docs/Reports/skill-portfolio-G1-2026-09-21.md`）
/// 的 165 / 1735 两个数就是从这个函数的输出算出来的。任何一条被改坏，G4 的验收数字都会**静默**换一套口径
/// （数字照样算得出来），所以每条都必须能在实现被改坏时变红。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillKeywordNormalizationTests
{
    // ───────────────────── 四个来源（Keywords → Tags → SkillId → Name → Name 分词） ─────────────────────

    [TestMethod]
    public void Collect_ShouldTakeKeywordsTagsSkillIdAndNameTokensTogether()
    {
        var collected = SkillKeywordNormalization.Collect(
            keywords: ["alpha"],
            tags: ["auto-generated"],
            skillId: "skill-a",
            name: "beta gamma");

        CollectionAssert.AreEquivalent(
            new[] { "alpha", "auto-generated", "skill-a", "beta gamma", "beta", "gamma" },
            collected.ToList(),
            "四个来源必须同时在集合里（少任何一路都会让关键词归属统计偏小）。");
    }

    [TestMethod]
    public void Collect_ShouldDeduplicateCaseInsensitively()
    {
        var collected = SkillKeywordNormalization.Collect(
            keywords: ["Alpha", "alpha"],
            tags: ["ALPHA"],
            skillId: "alpha",
            name: string.Empty);

        Assert.HasCount(1, collected, "大小写不同的同一关键词必须归一为一条，否则『共享关键词数』会被虚增。");
        Assert.Contains("alpha", collected);
    }

    // ───────────────────── 刻意保留的两个"不干净"之处（⛔ 不得顺手清理） ─────────────────────

    [TestMethod]
    public void Collect_ShouldAlwaysIncludeSkillId_EvenWhenBlank()
    {
        var collected = SkillKeywordNormalization.Collect(
            keywords: null,
            tags: null,
            skillId: "   ",
            name: null);

        Assert.HasCount(1, collected);
        Assert.Contains("   ", collected, "原实现无条件加入 skillId；在此处过滤会改变『声明了多少槽位』的口径。");
        Assert.IsFalse(SkillKeywordNormalization.IsUsableKeyword("   "), "可用性过滤是调用方的事（map 构建处）。");
    }

    [TestMethod]
    public void Collect_ShouldSplitName_AndDropSingleCharacterTokens()
    {
        var collected = SkillKeywordNormalization.Collect(
            keywords: null,
            tags: null,
            skillId: "skill-a",
            name: "alpha、beta|g");

        CollectionAssert.AreEquivalent(
            new[] { "skill-a", "alpha、beta|g", "alpha", "beta" },
            collected.ToList(),
            "长度 1 的分词片段（g）必须被丢弃；分隔符必须真的生效。");
    }

    [TestMethod]
    public void Collect_ShouldAddFullName_EvenWhenEveryTokenIsTooShort()
    {
        var collected = SkillKeywordNormalization.Collect(
            keywords: null,
            tags: null,
            skillId: "skill-a",
            name: "a b");

        CollectionAssert.AreEquivalent(
            new[] { "skill-a", "a b" },
            collected.ToList(),
            "分词全被丢弃时，Name 全句仍然是兜底关键词。");
    }

    // ───────────────────── 分隔符：与家族聚类口径必须同源 ─────────────────────

    [TestMethod]
    public void NameTokenSeparators_ShouldEqualFamilyTokenizationSeparators()
    {
        // 两处表达同一份字符集（都源自同一个生产实现）。只改一处 ⇒ 家族聚类与关键词归属用两套分词规则，
        // 且不会有任何断言失败 —— 故此处显式钉住相等性。
        CollectionAssert.AreEqual(
            SkillNameTokenization.StandardSeparators.ToList(),
            SkillKeywordNormalization.NameTokenSeparators.ToList());
    }

    [TestMethod]
    public void MinimumNameTokenLength_ShouldBeTwo()
    {
        // 原实现是 trimmed.Length > 1，即长度 1 被丢弃。
        Assert.AreEqual(2, SkillKeywordNormalization.MinimumNameTokenLength);

        var withSingleCharToken = SkillKeywordNormalization.Collect(null, null, "s", "a bc");
        Assert.IsFalse(withSingleCharToken.Contains("a"));
        Assert.Contains("bc", withSingleCharToken);
    }

    // ───────────────────── 可用性过滤（map 构建处唯一的口径来源） ─────────────────────

    [TestMethod]
    public void IsUsableKeyword_ShouldRejectNullEmptyAndWhitespace()
    {
        Assert.IsFalse(SkillKeywordNormalization.IsUsableKeyword(null));
        Assert.IsFalse(SkillKeywordNormalization.IsUsableKeyword(string.Empty));
        Assert.IsFalse(SkillKeywordNormalization.IsUsableKeyword(" \t "));
        Assert.IsTrue(SkillKeywordNormalization.IsUsableKeyword("alpha"));
        Assert.IsTrue(SkillKeywordNormalization.IsUsableKeyword("  alpha  "), "仅去空白判定，不做 trim（否则关键词会被改写）。");
    }

    [TestMethod]
    public void KeywordComparer_ShouldBeCaseInsensitiveOrdinal()
    {
        Assert.IsTrue(SkillKeywordNormalization.KeywordComparer.Equals("Alpha", "alpha"));
        Assert.IsFalse(SkillKeywordNormalization.KeywordComparer.Equals("Alpha", "alphb"));
    }
}
