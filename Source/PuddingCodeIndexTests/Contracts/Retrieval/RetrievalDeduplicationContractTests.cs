using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A13 / A14：跨 scope 去重先于过载判定（ADR-089 §2.4 / §8.10）+ 双视图（§5.4）。
/// </summary>
[TestClass]
public sealed class RetrievalDeduplicationContractTests
{
    /// <summary>A13：结构性重复必须先按符号身份去重，再去判过载。</summary>
    [TestMethod]
    public void A13_Cross_Scope_Dedup_Precedes_Overload_Judgement()
    {
        // 实测事实（§2.4）：仓库根 + 3 个子目录各被注册为项目 ⇒ 同一符号以 3 个 scope 各出现一次
        var scopes = new[] { "b375fee0-root", "scope-0ca100c528ef", "scope-ee887ff5297f" };
        var duplicated = new List<RetrievalHit>();
        for (var i = 0; i < 40; i++)
        {
            foreach (var scope in scopes)
                duplicated.Add(RetrievalTestData.TextHit(i, scopes: [scope]));
        }

        Assert.HasCount(120, duplicated, "阳性对照：未去重时确有 120 条（40 符号 × 3 scope）");

        var request = RetrievalTestData.Request(pageSize: 6);
        var deduped = RetrievalHitDeduplicator.Deduplicate(duplicated, request.Intent);

        // ③（先断言**首选主张**：去重先于过载判定）
        // 去重前会报 Low（结构性膨胀被误判成查询过泛）；去重后不得报
        var beforeFacts = RetrievalOverloadFacts.FromHits(
            duplicated.Count,
            request.PageSize,
            request.Intent,
            duplicated);
        var before = RetrievalOverloadDiagnostics.Evaluate(beforeFacts);
        Assert.AreEqual(QuerySpecificity.Low, before.Specificity, "去重前：结构性膨胀会被误判为查询过泛");
        Assert.Contains(OverloadReasonKind.VolumeOverBudget, before.OverloadReasons);
        Assert.IsTrue(Math.Abs(beforeFacts.HitsPerFile - 3.0) < 1e-9, "去重前 hits/files = 3.0");

        var afterFacts = RetrievalOverloadFacts.FromHits(
            deduped.DistinctSymbolCount,
            request.PageSize,
            request.Intent,
            deduped.Hits);
        Assert.IsFalse(afterFacts.IsVolumeOverBudget, "去重后 40 / 6 < 20 ⇒ 不再超倍率");
        Assert.IsTrue(Math.Abs(afterFacts.HitsPerFile - 1.0) < 1e-9, "去重后 hits/files = 1.0");
        var after = RetrievalOverloadDiagnostics.Evaluate(afterFacts);
        Assert.AreNotEqual(QuerySpecificity.Low, after.Specificity, "去重后不得再被判为查询过泛");

        // ② TotalCount 是去重后的数
        Assert.AreEqual(40, deduped.DistinctSymbolCount);

        // ① 对外 hits 中该符号只出现一次；多 scope 体现为元数据/置信度加权
        Assert.HasCount(40, deduped.Hits, "同一符号只出现一次");
        Assert.AreEqual(120, deduped.InputCount);
        Assert.AreEqual(80, deduped.RemovedDuplicateCount);
        Assert.IsTrue(deduped.Hits.All(hit => hit.ScopeIds.Count == 3), "多 scope 必须落在同一命中的 scope 元数据上");
        Assert.IsTrue(deduped.Hits.All(hit => Math.Abs(hit.ScopeCorroborationWeight - 1.5) < 1e-9), "3 个 scope ⇒ 佐证权重 1.5");
        Assert.IsTrue(deduped.Hits.All(hit => hit.Scopes.IsCorroborated));
        Assert.IsTrue(
            deduped.Hits.Select(hit => hit.Identity.Key).Distinct(StringComparer.Ordinal).Count() == deduped.Hits.Count,
            "去重后身份必须唯一");

        // ④ 嵌套/重叠 scope 必须能被检测并产出警告
        var registered = new[]
        {
            new RetrievalScopeDescriptor("b375fee0-root", "E:/github/AgentNetworkPlan/PuddingAgent"),
            new RetrievalScopeDescriptor("scope-0ca100c528ef", "E:/github/AgentNetworkPlan/PuddingAgent/Source/PuddingCore"),
            new RetrievalScopeDescriptor("scope-ee887ff5297f", "E:/github/AgentNetworkPlan/PuddingAgent/Source/PuddingPlatform"),
            new RetrievalScopeDescriptor("scope-6526fb344e33", "E:/github/AgentNetworkPlan/PuddingAgent/Source/PuddingRuntime"),
        };

        var warnings = ScopeOverlapDetector.Detect(registered);
        Assert.HasCount(3, warnings, "仓库根与 3 个子目录 ⇒ 3 条嵌套警告");
        Assert.IsTrue(warnings.All(warning => warning.Outer.ScopeId == "b375fee0-root"), "外层必须是仓库根");
        Assert.IsFalse(warnings.Any(warning => warning.SameRoot));
        Assert.IsFalse(string.IsNullOrWhiteSpace(warnings[0].Message));

        // 负面对照 1：仅前缀相似但不是子目录（repo vs repo2）不得被当成嵌套
        Assert.IsEmpty(ScopeOverlapDetector.Detect(
        [
            new RetrievalScopeDescriptor("root", "E:/repo"),
            new RetrievalScopeDescriptor("lookalike", "E:/repo2"),
        ]));

        // 负面对照 2：兄弟目录之间不得互相告警
        Assert.IsEmpty(ScopeOverlapDetector.Detect(
        [
            new RetrievalScopeDescriptor("core", "E:/repo/Source/PuddingCore"),
            new RetrievalScopeDescriptor("runtime", "E:/repo/Source/PuddingRuntime"),
        ]));

        // 同根重复注册（大小写不同）也必须告警
        var sameRoot = ScopeOverlapDetector.Detect(
        [
            new RetrievalScopeDescriptor("a", "E:/repo"),
            new RetrievalScopeDescriptor("b", "E:\\Repo"),
        ]);
        Assert.HasCount(1, sameRoot);
        Assert.IsTrue(sameRoot[0].SameRoot);

        // ⑤ 结果构造拒绝未去重的序列（去重必须先于构造）
        var wideRequest = RetrievalTestData.Request(pageSize: 120);
        Assert.ThrowsExactly<InvalidOperationException>(() => RetrievalResult.Create(
            wideRequest,
            duplicated,
            duplicated.Count,
            RetrievalTestData.IndexVersion));
    }

    /// <summary>A14：双视图（symbols / files）必须各自带命中原因摘要（ADR-089 §5.4）。</summary>
    [TestMethod]
    public void A14_Dual_Views_Carry_Hit_Reason()
    {
        var declaration = RetrievalTestData.Hit(
            "Sym.Dispatch",
            filePath: "Source/A.cs",
            line: 10,
            hitKind: RetrievalHitKind.Declaration,
            score: 3.0);
        var referenceInSameFile = RetrievalTestData.Hit(
            "Sym.Other",
            filePath: "Source/A.cs",
            line: 20,
            hitKind: RetrievalHitKind.Reference,
            score: 2.0,
            relation: RetrievalRelationKind.References);
        var referenceInOtherFile = RetrievalTestData.Hit(
            "Sym.Message",
            filePath: "Source/B.cs",
            line: 5,
            hitKind: RetrievalHitKind.Reference,
            score: 1.0,
            relation: RetrievalRelationKind.References,
            why: "引用处：字段类型 MessageDeliveryDispatcher");

        var hits = new[] { declaration, referenceInSameFile, referenceInOtherFile };
        RetrievalHitOrdering.EnsureOrdered(hits, RetrievalIntent.Auto);
        RetrievalHitOrdering.EnsureUniqueIdentities(hits);

        var symbols = RetrievalHitViews.Symbols(hits);
        var files = RetrievalHitViews.Files(hits);

        Assert.HasCount(3, symbols, "3 个命中身份 ⇒ 符号视图 3 条");
        Assert.HasCount(2, files, "2 个文件 ⇒ 文件视图 2 条");
        Assert.IsTrue(symbols.All(view => !string.IsNullOrWhiteSpace(view.Reason)), "符号视图必须带命中原因摘要");
        Assert.IsTrue(files.All(view => !string.IsNullOrWhiteSpace(view.Reason)), "文件视图必须带命中原因摘要");
        Assert.IsTrue(symbols.All(view => view.HitCount == 1));

        var sourceA = files.Single(view => view.FilePath == "source/a.cs");
        Assert.AreEqual(2, sourceA.HitCount, "同一文件的两条命中要在文件视图里合并计数");
        Assert.AreEqual(RetrievalHitKind.Declaration, sourceA.BestHitKind, "最佳命中层取全序第一条");
        Assert.AreEqual(RetrievalHitKind.Declaration, symbols[0].BestHitKind);

        // 结果上直接可取
        var request = RetrievalTestData.Request();
        var result = RetrievalResult.Create(request, hits, hits.Length, RetrievalTestData.IndexVersion);
        Assert.HasCount(3, result.Symbols);
        Assert.HasCount(2, result.Files);
        Assert.AreEqual(hits.Length, result.Hits.Count);
        Assert.AreEqual(hits.Length, result.TotalCount);
        Assert.IsFalse(result.Distribution.IsEmpty);
        StringAssert.Contains(result.Distribution.Describe(), "hitKind=");
        StringAssert.Contains(result.Distribution.Describe(), "language=csharp=3");
    }
}
