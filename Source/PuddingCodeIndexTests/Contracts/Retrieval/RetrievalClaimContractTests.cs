using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试：空结果必须有**机器可判定**的否定面（ADR-089 §8.x / 施工计划 R0-01）。
/// <para>
/// 背景：只改提示句不算交付 —— Agent 与前端读的是字段，不是散文。
/// 因此这里断言的是 <see cref="RetrievalClaim"/> 的取值，而不是文案。
/// </para>
/// </summary>
[TestClass]
public sealed class RetrievalClaimContractTests
{
    private static readonly SymbolAnchor Anchor = new("proj-alpha", "T:Acme.Service");

    /// <summary>所有 auto 零结果都禁止否定：词面没命中 ≠ 概念不存在（vector 面未接线）。</summary>
    [TestMethod]
    public void Auto_ZeroHits_Is_Never_Absence()
    {
        var request = RetrievalTestData.Request(query: "签发符号引用的令牌", intent: RetrievalIntent.Auto);

        var claim = RetrievalClaim.ClassifyForEmpty(request, RetrievalEmptyReason.GenuinelyAbsent);

        Assert.AreEqual(RetrievalClaimKind.None, claim.Kind, "auto 零结果不得形成否定。");
        CollectionAssert.Contains(claim.SearchedSurfaces.ToArray(), RetrievalClaim.SurfaceLexical);
        CollectionAssert.Contains(claim.UnsearchedSurfaces.ToArray(), RetrievalClaim.SurfaceVector,
            "语义面未接线时必须声明未查 —— 这是「不能下否定」的机器可读理由。");
        CollectionAssert.Contains(claim.RequiredProviders.ToArray(), RetrievalClaim.SurfaceVector,
            "auto 面形成确定性结论需要 vector provider。");
    }

    /// <summary>正对照：锚点关系面零边 ⇒ 限定范围结论（否则实现可以靠「永远报 partial」蒙混）。</summary>
    [TestMethod]
    public void Anchored_Relation_ZeroHits_Is_NoMatchInScope()
    {
        var request = new RetrievalRequest(
            query: string.Empty,
            scopeId: RetrievalTestData.DefaultScopeId,
            intent: RetrievalIntent.Callers,
            anchor: Anchor);

        var claim = RetrievalClaim.ClassifyForEmpty(request, RetrievalEmptyReason.GenuinelyAbsent);

        Assert.AreEqual(RetrievalClaimKind.NoMatchInScope, claim.Kind);
        Assert.IsEmpty(claim.UnsearchedSurfaces.ToArray(),
            "关系边来自索引、不依赖向量 ⇒ 该面已穷尽，允许限定范围结论。");
        CollectionAssert.Contains(claim.SearchedSurfaces.ToArray(), RetrievalClaim.SurfaceRelations);
        CollectionAssert.DoesNotContain(claim.RequiredProviders.ToArray(), RetrievalClaim.SurfaceVector);
    }

    /// <summary>显式非 auto 且无锚点 ⇒ 未找到证据（NoCandidates），仍不是「没有实现」。</summary>
    [TestMethod]
    public void Explicit_Intent_NoAnchor_Is_NoCandidates()
    {
        var request = RetrievalTestData.Request(intent: RetrievalIntent.Type);

        var claim = RetrievalClaim.ClassifyForEmpty(request, RetrievalEmptyReason.GenuinelyAbsent);

        Assert.AreEqual(RetrievalClaimKind.NoCandidates, claim.Kind);
        CollectionAssert.Contains(claim.UnsearchedSurfaces.ToArray(), RetrievalClaim.SurfaceVector);
    }

    /// <summary>索引不可用 / 能力不足 / 过滤性空：都不是否定。</summary>
    [TestMethod]
    public void NonAbsence_Reasons_Are_Never_Negation()
    {
        var request = RetrievalTestData.Request(intent: RetrievalIntent.Type);

        foreach (var reason in new[]
                 {
                     RetrievalEmptyReason.NotIndexed,
                     RetrievalEmptyReason.IndexCorrupted,
                     RetrievalEmptyReason.LanguageUnsupported,
                     RetrievalEmptyReason.IntentUnsupported,
                     RetrievalEmptyReason.FilteredOut,
                 })
        {
            Assert.AreEqual(
                RetrievalClaimKind.None,
                RetrievalClaim.ClassifyForEmpty(request, reason).Kind,
                $"{reason} 不得形成否定。");
        }
    }

    /// <summary>结果层：0 命中时声明必非空（未显式给出则按请求+原因保守推导）。</summary>
    [TestMethod]
    public void Empty_Result_Derives_Claim_When_Not_Given()
    {
        var request = RetrievalTestData.Request(query: "签发符号引用的令牌", intent: RetrievalIntent.Auto);

        var result = RetrievalResult.Empty(
            request,
            RetrievalEmptyReason.GenuinelyAbsent,
            RetrievalNextStep.AcceptAbsence("词面未命中"),
            RetrievalTestData.IndexVersion);

        Assert.IsNotNull(result.Claim, "0 命中必须带机器可判定的否定面。");
        Assert.AreEqual(RetrievalClaimKind.None, result.Claim!.Kind, "auto 零结果不得被读成「没有」。");
    }

    /// <summary>结果层：有命中时不得同时声明否定面（否则自相矛盾）。</summary>
    [TestMethod]
    public void Hit_Result_Has_No_Claim()
    {
        var request = RetrievalTestData.Request();
        var hits = RetrievalTestData.Page(2);

        var result = RetrievalResult.Create(request, hits, 2, RetrievalTestData.IndexVersion);

        Assert.IsNull(result.Claim, "有命中时不应带否定声明。");
    }

    /// <summary>锚点 + 精确零匹配 ⇒ 结果层应给出 NoMatchInScope（正对照在结果层的体现）。</summary>
    [TestMethod]
    public void Anchored_Empty_Result_Carries_NoMatchInScope()
    {
        var request = new RetrievalRequest(
            query: string.Empty,
            scopeId: RetrievalTestData.DefaultScopeId,
            intent: RetrievalIntent.Callers,
            anchor: Anchor);

        var result = RetrievalResult.Empty(
            request,
            RetrievalEmptyReason.GenuinelyAbsent,
            RetrievalNextStep.AcceptAbsence("该锚点范围内没有此类关系边"),
            RetrievalTestData.IndexVersion);

        Assert.IsNotNull(result.Claim);
        Assert.AreEqual(RetrievalClaimKind.NoMatchInScope, result.Claim!.Kind);
    }
}
