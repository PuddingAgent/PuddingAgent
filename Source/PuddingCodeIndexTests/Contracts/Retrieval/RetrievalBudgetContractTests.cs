using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A8：单次返回必须有界（条数 + 字节/token 双预算，token 优先 —— ADR-089 §8.7）。
/// </summary>
[TestClass]
public sealed class RetrievalBudgetContractTests
{
    /// <summary>A8：命中远超页大小 ⇒ 返回条数 ≤ PageSize，且双预算都未超；超预算的页构造即被拒。</summary>
    [TestMethod]
    public void A08_Single_Page_Is_Bounded_By_PageSize_And_Budgets()
    {
        var request = RetrievalTestData.Request(pageSize: 10, maxBytes: 4096, maxTokens: 1024);
        var page = RetrievalTestData.Page(10);
        const int totalCount = 300; // 远超页大小

        var result = RetrievalTestData.Truncated(request, page, totalCount);

        Assert.HasCount(10, result.Hits, "返回条数不得超过页大小");
        Assert.IsTrue(result.Hits.Count <= request.PageSize, "返回条数 ≤ PageSize");
        Assert.IsTrue(result.BytesUsed <= request.MaxBytes, "字节预算必须未超");
        Assert.IsTrue(result.TokensUsed <= request.MaxTokens, "token 预算必须未超（优先预算）");
        Assert.IsTrue(result.TokensUsed > 0, "阳性对照：本例确实计到了 token（否则上一条是空断言）");

        // 页比 PageSize 大 ⇒ 构造即拒（"给我全部"不可表示）
        var oversizedPage = RetrievalTestData.Page(11);
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Create(
            request,
            oversizedPage,
            11,
            RetrievalTestData.IndexVersion));

        // 预算不够装下这一页 ⇒ 必须落盘分页，而不是超预算返回
        var tinyBudget = RetrievalTestData.Request(pageSize: 10, maxBytes: 64, maxTokens: 64);
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalTestData.Truncated(tinyBudget, page, totalCount));

        // 反面：真实总数不得小于返回条数（事实自相矛盾）
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Create(
            request,
            page,
            3,
            RetrievalTestData.IndexVersion));

        // 页大小自身有界（1..MaxPageSize）
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetrievalTestData.Request(pageSize: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetrievalTestData.Request(pageSize: RetrievalBudget.MaxPageSize + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetrievalTestData.Request(maxTokens: 0));

        // 估算口径：单调、非负（本刀只用它做"有界"判据，不当作计量）
        Assert.IsTrue(RetrievalBudget.EstimateTokens(1) >= 1);
        Assert.IsTrue(RetrievalBudget.EstimateTokens(0) == 0);
        Assert.IsTrue(
            RetrievalBudget.EstimatePayloadBytes("abcdef") > RetrievalBudget.EstimatePayloadBytes("abc"),
            "载荷越长估算字节越大");
    }
}
