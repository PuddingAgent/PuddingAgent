using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A5：显式全序 + 确定性（ADR-089 §2.1 第 5 条；分页游标的前提）。
/// </summary>
[TestClass]
public sealed class RetrievalRankingContractTests
{
    /// <summary>
    /// A5：比较器给出**稳定且唯一**的顺序；打乱输入后排序结果逐位相同（跑多次）。
    /// 语料刻意让每一级 tie-break 都被用到，末级（身份键）是 M4 的取红点。
    /// </summary>
    [TestMethod]
    public void A05_Comparer_Is_A_Deterministic_Total_Order()
    {
        var declaration = RetrievalTestData.Hit(
            "Sym.Decl",
            filePath: "Source/Proj/C.cs",
            line: 10,
            hitKind: RetrievalHitKind.Declaration,
            score: 2.0);
        var pathB = RetrievalTestData.Hit(
            "Sym.Path.B",
            filePath: "Source/Proj/B.cs",
            line: 10,
            hitKind: RetrievalHitKind.Reference,
            score: 2.0,
            relation: RetrievalRelationKind.References);
        var sameFileLine10 = RetrievalTestData.Hit(
            "Sym.Line.10",
            filePath: "Source/Proj/Same.cs",
            line: 10,
            hitKind: RetrievalHitKind.Reference,
            score: 2.0,
            relation: RetrievalRelationKind.References);
        var sameFileLine20 = RetrievalTestData.Hit(
            "Sym.Line.20",
            filePath: "Source/Proj/Same.cs",
            line: 20,
            hitKind: RetrievalHitKind.Reference,
            score: 2.0,
            relation: RetrievalRelationKind.References);
        var tieAlpha = RetrievalTestData.Hit(
            "Sym.Alpha",
            filePath: "Source/Proj/Tie.cs",
            line: 7,
            hitKind: RetrievalHitKind.Reference,
            score: 2.0,
            symbolName: "Tie",
            relation: RetrievalRelationKind.References);
        var tieBeta = RetrievalTestData.Hit(
            "Sym.Beta",
            filePath: "Source/Proj/Tie.cs",
            line: 7,
            hitKind: RetrievalHitKind.Reference,
            score: 2.0,
            symbolName: "Tie",
            relation: RetrievalRelationKind.References);
        var lowScore = RetrievalTestData.Hit(
            "Sym.Low",
            filePath: "Source/Proj/D.cs",
            line: 5,
            hitKind: RetrievalHitKind.Declaration,
            score: 1.0);

        // 前置：末级 tie-break 的两个命中，其前五级键**全部相同**（否则"身份键承重"无从证明）
        Assert.AreEqual(tieAlpha.Evidence.NormalizedFilePath, tieBeta.Evidence.NormalizedFilePath);
        Assert.AreEqual(tieAlpha.Evidence.Line, tieBeta.Evidence.Line);
        Assert.AreEqual(tieAlpha.SymbolName, tieBeta.SymbolName);
        Assert.AreEqual(tieAlpha.EffectiveRank, tieBeta.EffectiveRank);
        Assert.AreEqual(tieAlpha.HitKind, tieBeta.HitKind);
        Assert.AreNotEqual(tieAlpha.Identity.Key, tieBeta.Identity.Key, "两个命中的身份必须不同（否则它们本就不可区分）");

        var hits = new[] { tieBeta, sameFileLine20, declaration, lowScore, tieAlpha, sameFileLine10, pathB };

        var comparer = RetrievalHitComparer.Auto;
        var expected = comparer.Sort(hits).ToArray();

        // 成文的期望顺序：分数降序 → 层序升序 → 路径 → 行号 → 符号名 → 身份
        CollectionAssert.AreEqual(
            new[] { "Sym.Decl", "Sym.Path.B", "Sym.Line.10", "Sym.Line.20", "Sym.Alpha", "Sym.Beta", "Sym.Low" },
            expected.Select(hit => hit.Identity.SymbolId).ToArray(),
            "键链必须逐级生效（含末级身份键）");

        // 打乱多次 ⇒ 结果逐位相同
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var shuffled = hits
                .OrderBy(_ => Random.Shared.Next())
                .ToArray();
            var actual = comparer.Sort(shuffled).ToArray();
            CollectionAssert.AreEqual(expected, actual, $"第 {attempt + 1} 次打乱后排序必须与首次逐位相同");
        }

        // 顺序断言本身必须能取红：逆序序列必须被 EnsureOrdered 拒绝
        var reversed = expected.Reverse().ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(
            () => RetrievalHitOrdering.EnsureOrdered(reversed, RetrievalIntent.Auto),
            "逆序序列必须被测出越序");
        RetrievalHitOrdering.EnsureOrdered(expected, RetrievalIntent.Auto);

        // 层序随意图而变：References 意图下同分时引用层先出（见 A7），此处确认 Auto 下并非如此
        Assert.AreEqual(RetrievalHitKind.Declaration, expected[0].HitKind);
    }

    /// <summary>多 scope 佐证加权必须确定性且单调（§2.4："多 scope 作为置信度加权"）。</summary>
    [TestMethod]
    public void A05b_Scope_Corroboration_Weight_Is_Deterministic_And_Monotonic()
    {
        var one = RetrievalTestData.TextHit(1, scopes: ["scope-a"]);
        var two = RetrievalTestData.TextHit(1, scopes: ["scope-a", "scope-b"]);
        var three = RetrievalTestData.TextHit(1, scopes: ["scope-a", "scope-b", "scope-c"]);

        Assert.AreEqual(1.0, one.ScopeCorroborationWeight, 1e-9);
        Assert.AreEqual(1.25, two.ScopeCorroborationWeight, 1e-9);
        Assert.AreEqual(1.5, three.ScopeCorroborationWeight, 1e-9);

        Assert.AreEqual(one.Score, one.EffectiveRank, 1e-9);
        Assert.IsTrue(three.EffectiveRank > two.EffectiveRank, "佐证越多排序键越大（同一分数下）");
        Assert.IsTrue(two.EffectiveRank > one.EffectiveRank);

        // 重复 scope 标识必须被折叠（否则权重会被重复计数）
        var duplicated = RetrievalTestData.TextHit(1, scopes: ["scope-a", "scope-a", "scope-a"]);
        Assert.HasCount(1, duplicated.ScopeIds);
        Assert.AreEqual(1.0, duplicated.ScopeCorroborationWeight, 1e-9);

        // 取值相等：两处独立构造的相同命中必须相等（集合被压成规范键）
        var rebuilt = RetrievalTestData.TextHit(1, scopes: ["scope-b", "scope-a"]);
        Assert.AreEqual(two, rebuilt, "scope 顺序不同但集合相同时，命中必须相等（规范化在构造期完成）");
    }
}
