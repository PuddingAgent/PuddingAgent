using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A12：过滤性空 ≠ 真空（ADR-089 §2.3 硬约束 / §8.9），以及空原因裁决表。
/// </summary>
[TestClass]
public sealed class RetrievalEmptyReasonContractTests
{
    /// <summary>A12：因过滤导致 0 命中 ⇒ FilteredOut + "放宽哪个面"；无过滤确实不存在 ⇒ GenuinelyAbsent。</summary>
    [TestMethod]
    public void A12_Filtered_Empty_Is_Not_Absence()
    {
        // 用户第七轮举证的两个面：只关注类名称 / 只取 Class
        var matchTargetFilter = RetrievalFilter.ForMatchTargets(RetrievalMatchTarget.SymbolName);
        var kindFilter = RetrievalFilter.ForSymbolKinds(CodeSymbolKind.Class);
        var directoryFilter = RetrievalFilter.ForDirectory("Source/PuddingRuntime");

        // ① 过滤性空必须是 FilteredOut（不得报成 GenuinelyAbsent）
        Assert.AreEqual(
            RetrievalEmptyReason.FilteredOut,
            RetrievalEmptyReasonPolicy.ClassifyForZeroHits(matchTargetFilter));
        Assert.AreEqual(
            RetrievalEmptyReason.FilteredOut,
            RetrievalEmptyReasonPolicy.ClassifyForZeroHits(kindFilter));

        // ② 必须说明"放宽哪个面会得到结果"
        Assert.AreEqual(RetrievalFacet.MatchTarget, RetrievalEmptyReasonPolicy.SuggestRelaxationFacet(matchTargetFilter));
        Assert.AreEqual(RetrievalFacet.SymbolKind, RetrievalEmptyReasonPolicy.SuggestRelaxationFacet(kindFilter));
        Assert.Contains(
            RetrievalEmptyReasonPolicy.SuggestRelaxationFacet(directoryFilter),
            directoryFilter.ConstrainedFacets(),
            "放宽的面必须是实际生效的面之一");

        // 反向对照：无过滤 + 确实不存在 ⇒ GenuinelyAbsent；且"放宽"无从谈起
        Assert.IsFalse(RetrievalFilter.None.HasAnyConstraint);
        Assert.AreEqual(
            RetrievalEmptyReason.GenuinelyAbsent,
            RetrievalEmptyReasonPolicy.ClassifyForZeroHits(RetrievalFilter.None));
        Assert.AreEqual(
            RetrievalEmptyReason.GenuinelyAbsent,
            RetrievalEmptyReasonPolicy.ClassifyForZeroHits(null));
        Assert.ThrowsExactly<RetrievalContractViolationException>(
            () => RetrievalEmptyReasonPolicy.SuggestRelaxationFacet(RetrievalFilter.None),
            "没有生效面时不得凭空编一个可放宽的面");

        // ③ 结果层：带过滤面的请求报 FilteredOut 必须给"放宽"建议，并回显生效的面
        var filteredRequest = RetrievalTestData.Request(filter: matchTargetFilter);
        var relaxStep = RetrievalNextStep.Relax(RetrievalFacet.MatchTarget, "放宽匹配域：允许在签名/正文里匹配");
        var filteredResult = RetrievalResult.Empty(
            filteredRequest,
            RetrievalEmptyReason.FilteredOut,
            relaxStep,
            RetrievalTestData.IndexVersion,
            appliedFilters: [matchTargetFilter.Describe()]);

        Assert.AreEqual(RetrievalEmptyReason.FilteredOut, filteredResult.EmptyReason);
        Assert.HasCount(1, filteredResult.NextSteps);
        Assert.AreEqual(RetrievalNextStepKind.RelaxFilter, filteredResult.NextStep!.Kind);
        Assert.AreEqual(RetrievalFacet.MatchTarget, filteredResult.NextStep.Relaxing);
        Assert.HasCount(1, filteredResult.AppliedFilters, "必须回显\"我替你加了哪些面\"（§2.3）");
        StringAssert.Contains(filteredResult.AppliedFilters[0], "match:");

        // ④ 反向对照：带过滤面却报"确实不存在" ⇒ 拒绝（这正是"过滤性空被报成真空"）
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Empty(
            filteredRequest,
            RetrievalEmptyReason.GenuinelyAbsent,
            RetrievalNextStep.AcceptAbsence("确实没有"),
            RetrievalTestData.IndexVersion));

        // ⑤ 放宽的面必须是请求里实际生效的面（凭空造一个没开的面 ⇒ 拒绝）
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Empty(
            filteredRequest,
            RetrievalEmptyReason.FilteredOut,
            RetrievalNextStep.Relax(RetrievalFacet.Directory, "放宽目录"),
            RetrievalTestData.IndexVersion));

        // ⑥ 报 FilteredOut 但请求根本没过滤面 ⇒ 拒绝（那是真空，不是过滤性空）
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Empty(
            RetrievalTestData.Request(),
            RetrievalEmptyReason.FilteredOut,
            relaxStep,
            RetrievalTestData.IndexVersion));

        // ⑦ 无过滤 + 确实不存在 ⇒ 正向路径可用
        var absent = RetrievalResult.Empty(
            RetrievalTestData.Request(),
            RetrievalEmptyReason.GenuinelyAbsent,
            RetrievalNextStep.AcceptAbsence("过滤面干净、索引可用 ⇒ 确实不存在"),
            RetrievalTestData.IndexVersion);
        Assert.AreEqual(RetrievalEmptyReason.GenuinelyAbsent, absent.EmptyReason);

        // ⑧ 裁决优先级（成文）：索引不可用 / 能力不足是"关于世界的事实"，优先于过滤面
        Assert.AreEqual(
            RetrievalEmptyReason.NotIndexed,
            RetrievalEmptyReasonPolicy.ClassifyForZeroHits(matchTargetFilter, RetrievalIndexAvailability.NotIndexed));
        Assert.AreEqual(
            RetrievalEmptyReason.IndexCorrupted,
            RetrievalEmptyReasonPolicy.ClassifyForZeroHits(matchTargetFilter, RetrievalIndexAvailability.Corrupted));
        Assert.AreEqual(
            RetrievalEmptyReason.LanguageUnsupported,
            RetrievalEmptyReasonPolicy.ClassifyForZeroHits(
                matchTargetFilter,
                indexAvailability: RetrievalIndexAvailability.Available,
                capabilityLevel: RetrievalCapabilityLevel.None));
        Assert.AreEqual(
            RetrievalEmptyReason.IntentUnsupported,
            RetrievalEmptyReasonPolicy.ClassifyForZeroHits(matchTargetFilter, intentSupported: false));

        // ⑨ 索引缺失/损坏时，建议必须是"重建索引"（不得建议收窄）
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Empty(
            RetrievalTestData.Request(),
            RetrievalEmptyReason.NotIndexed,
            RetrievalTestData.NarrowStep(),
            RetrievalTestData.IndexVersion));

        var notIndexed = RetrievalResult.Empty(
            RetrievalTestData.Request(),
            RetrievalEmptyReason.NotIndexed,
            RetrievalNextStep.RebuildIndex("该 scope 尚未建立索引：先注册/重建索引，或改用文本面兜底"),
            RetrievalTestData.IndexVersion);
        Assert.AreEqual(RetrievalNextStepKind.RebuildIndex, notIndexed.NextStep!.Kind);
    }
}
