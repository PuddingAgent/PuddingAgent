using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A1 / A7（ADR-089 §2 + §2.1 反向要求）。
/// </summary>
[TestClass]
public sealed class RetrievalIntentContractTests
{
    /// <summary>
    /// A1：<c>Auto</c> 必须占 0 值位置 ⇒ 缺省即 Auto（用户第五轮裁定"auto 是默认设置，避免让 Agent 打转"）。
    /// </summary>
    [TestMethod]
    public void A01_Default_Intent_Is_Auto_And_Occupies_Zero()
    {
        Assert.AreEqual(0, (int)RetrievalIntent.Auto, "Auto 必须显式占 0 值位置");
        Assert.AreEqual(RetrievalIntent.Auto, default(RetrievalIntent), "default(RetrievalIntent) 必须是 Auto");
        Assert.IsTrue(RetrievalIntentPolicy.IsAuto(default), "缺省意图必须被判定为 Auto（不问意图）");
        Assert.IsFalse(RetrievalIntentPolicy.IsAuto(RetrievalIntent.References), "显式意图不得被判成 Auto");
        Assert.IsTrue(RetrievalIntentPolicy.IsExplicit(RetrievalIntent.References));
        Assert.IsFalse(RetrievalIntentPolicy.IsExplicit(RetrievalIntent.Auto));

        // 不给 intent 的请求就是 Auto（"不问意图"是结构性缺省，不靠调用方记得传参）
        var request = RetrievalTestData.Request();
        Assert.AreEqual(RetrievalIntent.Auto, request.Intent);
        Assert.IsTrue(request.IsAuto);

        // 成员集合冻结（12 个）：防止"插入成员改变 0 值"这类静默漂移
        Assert.HasCount(12, Enum.GetValues<RetrievalIntent>(), "RetrievalIntent 成员集合被冻结为 12 个");

        // 显式意图缺省值不得为 0（否则 default 会静默变成"某个人为的意图"）
        foreach (var intent in Enum.GetValues<RetrievalIntent>())
        {
            if (intent == RetrievalIntent.Auto)
                continue;

            Assert.AreNotEqual(0, (int)intent, $"显式意图 {intent} 不得占用 0 值");
        }
    }

    /// <summary>
    /// A7：显式 intent 的层序**不被 Auto 逻辑改写**；层序表必须成文（全序、可解释）。
    /// </summary>
    [TestMethod]
    public void A07_Explicit_Intent_Keeps_Its_Own_Layer_Order()
    {
        var allKinds = Enum.GetValues<RetrievalHitKind>();

        // 每张层序表都必须是 7 层的**全序**（不缺不重）——否则排序会退化成"部分可比"
        foreach (var intent in Enum.GetValues<RetrievalIntent>())
        {
            var order = RetrievalIntentPolicy.LayerOrder(intent);
            Assert.HasCount(allKinds.Length, order, $"intent={intent} 的层序表必须覆盖全部命中层");
            CollectionAssert.AreEquivalent(
                allKinds,
                order.ToArray(),
                $"intent={intent} 的层序表不得缺层或重复");
        }

        // 成文表逐条冻结
        var canonical = new[]
        {
            RetrievalHitKind.Declaration,
            RetrievalHitKind.Implementation,
            RetrievalHitKind.Reference,
            RetrievalHitKind.CallSite,
            RetrievalHitKind.DocMention,
            RetrievalHitKind.TextHit,
            RetrievalHitKind.Unknown,
        };
        CollectionAssert.AreEqual(canonical, RetrievalIntentPolicy.LayerOrder(RetrievalIntent.Auto).ToArray());
        CollectionAssert.AreEqual(canonical, RetrievalIntentPolicy.LayerOrder(RetrievalIntent.Any).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                RetrievalHitKind.Reference,
                RetrievalHitKind.CallSite,
                RetrievalHitKind.Implementation,
                RetrievalHitKind.Declaration,
                RetrievalHitKind.DocMention,
                RetrievalHitKind.TextHit,
                RetrievalHitKind.Unknown,
            },
            RetrievalIntentPolicy.LayerOrder(RetrievalIntent.References).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                RetrievalHitKind.Declaration,
                RetrievalHitKind.CallSite,
                RetrievalHitKind.Reference,
                RetrievalHitKind.Implementation,
                RetrievalHitKind.DocMention,
                RetrievalHitKind.TextHit,
                RetrievalHitKind.Unknown,
            },
            RetrievalIntentPolicy.LayerOrder(RetrievalIntent.Member).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                RetrievalHitKind.Implementation,
                RetrievalHitKind.Declaration,
                RetrievalHitKind.Reference,
                RetrievalHitKind.CallSite,
                RetrievalHitKind.DocMention,
                RetrievalHitKind.TextHit,
                RetrievalHitKind.Unknown,
            },
            RetrievalIntentPolicy.LayerOrder(RetrievalIntent.Implementation).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                RetrievalHitKind.DocMention,
                RetrievalHitKind.TextHit,
                RetrievalHitKind.Declaration,
                RetrievalHitKind.Reference,
                RetrievalHitKind.Implementation,
                RetrievalHitKind.CallSite,
                RetrievalHitKind.Unknown,
            },
            RetrievalIntentPolicy.LayerOrder(RetrievalIntent.Comment).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                RetrievalHitKind.TextHit,
                RetrievalHitKind.DocMention,
                RetrievalHitKind.Declaration,
                RetrievalHitKind.Implementation,
                RetrievalHitKind.Reference,
                RetrievalHitKind.CallSite,
                RetrievalHitKind.Unknown,
            },
            RetrievalIntentPolicy.LayerOrder(RetrievalIntent.File).ToArray());

        // 层序表必须真的不同，否则"显式意图优先"这条断言是空的
        Assert.AreNotEqual(
            RetrievalIntentPolicy.LayerOrder(RetrievalIntent.Auto)[0],
            RetrievalIntentPolicy.LayerOrder(RetrievalIntent.References)[0],
            "References 的首层必须与 Auto 不同（引用优先 vs 声明优先）");

        // 行为证明：同分情况下，Auto 让声明先出，References 让引用先出
        var referenceHit = RetrievalTestData.Hit(
            "Sym.Ref",
            filePath: "Source/Proj/A.cs",
            line: 20,
            hitKind: RetrievalHitKind.Reference,
            relation: RetrievalRelationKind.References);
        var declarationHit = RetrievalTestData.Hit(
            "Sym.Decl",
            filePath: "Source/Proj/B.cs",
            line: 10,
            hitKind: RetrievalHitKind.Declaration);
        var hits = new[] { referenceHit, declarationHit };

        var autoSorted = RetrievalHitComparer.Auto.Sort(hits);
        Assert.AreEqual(RetrievalHitKind.Declaration, autoSorted[0].HitKind, "Auto 下声明层优先");

        var referenceSorted = RetrievalHitComparer.ForIntent(RetrievalIntent.References).Sort(hits);
        Assert.AreEqual(
            RetrievalHitKind.Reference,
            referenceSorted[0].HitKind,
            "显式 References 必须用自己的层序（引用优先），不得被 Auto 逻辑改写");

        // intent → 过滤面 的成文表
        CollectionAssert.AreEqual(
            new[] { CodeSymbolKind.Class, CodeSymbolKind.Interface, CodeSymbolKind.Struct, CodeSymbolKind.Enum, CodeSymbolKind.Delegate, CodeSymbolKind.Type },
            RetrievalIntentPolicy.SymbolKindFilter(RetrievalIntent.Type)!.ToArray());
        CollectionAssert.AreEqual(
            new[] { CodeSymbolKind.Namespace },
            RetrievalIntentPolicy.SymbolKindFilter(RetrievalIntent.Namespace)!.ToArray());
        Assert.IsNull(RetrievalIntentPolicy.SymbolKindFilter(RetrievalIntent.Auto), "Auto 不得预置符号种类过滤（不问意图）");
        CollectionAssert.AreEqual(
            new[] { RetrievalRelationKind.Implements, RetrievalRelationKind.Overrides },
            RetrievalIntentPolicy.RelationFilter(RetrievalIntent.Implementation)!.ToArray());
        Assert.Contains(RetrievalRelationKind.References, RetrievalIntentPolicy.RelationFilter(RetrievalIntent.References)!);
        Assert.IsNull(RetrievalIntentPolicy.RelationFilter(RetrievalIntent.Auto));
        Assert.IsTrue(RetrievalIntentPolicy.IsLegitimateBulkIntent(RetrievalIntent.References));
        Assert.IsFalse(RetrievalIntentPolicy.IsLegitimateBulkIntent(RetrievalIntent.Auto));
    }
}
