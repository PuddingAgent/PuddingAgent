using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A10 / A11：过载即信号（ADR-089 §2.2 B/C、§8.8）+ 收窄手段必须是四类之一。
/// </summary>
[TestClass]
public sealed class RetrievalOverloadContractTests
{
    /// <summary>A10：过载判定成文可测，且**不得**把"正当的多"误报成"查询过泛"。</summary>
    [TestMethod]
    public void A10_Overload_Is_Low_Specificity_Except_Legitimate_Bulk()
    {
        // ① 超倍率 + 高度分散 + 词法主导 ⇒ Low，并逐条给出原因
        var overloadedFacts = RetrievalOverloadFacts.Create(
            totalCount: 200,
            pageSize: 10,
            distinctFileCount: 200,
            lexicalHitCount: 190,
            semanticHitCount: 10,
            distinctTopLevelDirectoryCount: 2,
            distinctHitKindCount: 2,
            intent: RetrievalIntent.Auto,
            distinctRelationKindCount: 3,
            distinctSymbolCount: 200);

        Assert.IsTrue(overloadedFacts.IsVolumeOverBudget, "200 / 10 = 20 倍 ⇒ 超阈值");
        Assert.IsTrue(overloadedFacts.IsHighlyDispersed, "hits/files = 1.0 ⇒ 高度分散");
        Assert.IsTrue(overloadedFacts.IsLexicalDominated, "词法占比 0.95 ⇒ 词法主导");

        var diagnostics = RetrievalOverloadDiagnostics.Evaluate(overloadedFacts);
        Assert.AreEqual(QuerySpecificity.Low, diagnostics.Specificity);
        Assert.IsTrue(diagnostics.IsOverloaded);
        Assert.HasCount(3, diagnostics.OverloadReasons);
        Assert.Contains(OverloadReasonKind.VolumeOverBudget, diagnostics.OverloadReasons);
        Assert.Contains(OverloadReasonKind.HighDispersion, diagnostics.OverloadReasons);
        Assert.Contains(OverloadReasonKind.LexicalDominance, diagnostics.OverloadReasons);

        // 分布附加信号（跨目录 / 命中层分散）必须也进原因集合
        var scatteredDiagnostics = RetrievalOverloadDiagnostics.Evaluate(RetrievalOverloadFacts.Create(
            200,
            10,
            200,
            190,
            10,
            distinctTopLevelDirectoryCount: 5,
            distinctHitKindCount: 3,
            RetrievalIntent.Auto,
            3,
            200));
        Assert.HasCount(5, scatteredDiagnostics.OverloadReasons);
        Assert.Contains(OverloadReasonKind.CrossDirectorySpread, scatteredDiagnostics.OverloadReasons);
        Assert.Contains(OverloadReasonKind.KindScatter, scatteredDiagnostics.OverloadReasons);

        // 诊断本身不得自相矛盾：Low 必须给原因、High 不得带原因
        Assert.ThrowsExactly<ArgumentException>(() => QueryDiagnostics.Create(QuerySpecificity.Low));
        Assert.ThrowsExactly<ArgumentException>(
            () => QueryDiagnostics.Create(QuerySpecificity.High, [OverloadReasonKind.VolumeOverBudget]));

        // ② 过载但**未截断**：仍必须恰好一条收窄建议
        var bulkPage = RetrievalTestData.Page(200);
        var wideRequest = RetrievalTestData.Request(pageSize: 200, maxBytes: 65536, maxTokens: 16384);
        var overloadedResult = RetrievalResult.Create(
            wideRequest,
            bulkPage,
            200,
            RetrievalTestData.IndexVersion,
            nextSteps: [RetrievalTestData.NarrowStep()],
            diagnostics: diagnostics);

        Assert.IsFalse(overloadedResult.IsTruncated, "本例刻意不截断，以便把\"建议来自过载\"与\"建议来自截断\"分开");
        Assert.IsTrue(overloadedResult.Diagnostics.IsOverloaded);
        Assert.HasCount(1, overloadedResult.NextSteps, "过载必须恰好一条下一步");
        Assert.AreEqual(RetrievalNextStepKind.NarrowQuery, overloadedResult.NextStep!.Kind);
        Assert.IsNotNull(overloadedResult.NextStep.Narrowing);

        // 过载却不给建议 ⇒ 拒绝（§8.8：不得让 Agent 靠再发一次查询发现自己的查询过泛）
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Create(
            wideRequest,
            bulkPage,
            200,
            RetrievalTestData.IndexVersion,
            diagnostics: diagnostics));

        // ③ 反向对照（误报守卫）
        // ③a 正当的多：References + 关系单一 + 符号明确 ⇒ 无论总量多大都不得报 Low
        var legitimateFacts = RetrievalOverloadFacts.Create(
            totalCount: 200,
            pageSize: 10,
            distinctFileCount: 50,
            lexicalHitCount: 20,
            semanticHitCount: 180,
            distinctTopLevelDirectoryCount: 3,
            distinctHitKindCount: 1,
            intent: RetrievalIntent.References,
            distinctRelationKindCount: 1,
            distinctSymbolCount: 1);

        Assert.IsTrue(legitimateFacts.IsLegitimateBulk, "References + 关系单一 + 符号明确 ⇒ 正当的多");
        Assert.IsTrue(legitimateFacts.IsVolumeOverBudget, "对照：本例确实超倍率（否则断言是空的）");
        var legitimateDiagnostics = RetrievalOverloadDiagnostics.Evaluate(legitimateFacts);
        Assert.AreNotEqual(QuerySpecificity.Low, legitimateDiagnostics.Specificity, "正当的多不得报 Low");
        Assert.IsEmpty(legitimateDiagnostics.OverloadReasons);

        // ③b 同一个"紧凑"事实在 Auto 下也不得报 Low（不依赖豁免也能拦住误报）
        var compactFacts = RetrievalOverloadFacts.Create(
            200,
            10,
            50,
            20,
            180,
            distinctTopLevelDirectoryCount: 3,
            distinctHitKindCount: 1,
            RetrievalIntent.Auto,
            1,
            1);

        Assert.IsFalse(compactFacts.IsLegitimateBulk, "Auto 不是\"正当的多\"的豁免意图");
        Assert.IsTrue(compactFacts.IsVolumeOverBudget, "对照：量确实大");
        Assert.IsFalse(compactFacts.IsHighlyDispersed, "hits/files = 4.0 ⇒ 不分散");
        Assert.IsFalse(compactFacts.IsLexicalDominated, "词法占比 0.1 ⇒ 非词法主导");
        var compactDiagnostics = RetrievalOverloadDiagnostics.Evaluate(compactFacts);
        Assert.AreNotEqual(QuerySpecificity.Low, compactDiagnostics.Specificity, "不分散且语义主导 ⇒ 不得报 Low");
        Assert.AreEqual(QuerySpecificity.Medium, compactDiagnostics.Specificity);

        // 事实自相矛盾的输入必须被拒（否则统计口径可以被伪造）
        Assert.ThrowsExactly<ArgumentException>(() => RetrievalOverloadFacts.Create(10, 10, 5, 8, 8, 1, 1, RetrievalIntent.Auto, 1, 5));
        Assert.ThrowsExactly<ArgumentException>(() => RetrievalOverloadFacts.Create(10, 10, 3, 1, 1, 1, 1, RetrievalIntent.Auto, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RetrievalOverloadFacts.Create(10, 0, 1, 1, 0, 1, 1, RetrievalIntent.Auto, 1, 1));
    }

    /// <summary>A11：收窄手段必须恰好四类（目录 / 文件类型 / 组合条件 / 显式 intent），且不可用别的载荷糊弄。</summary>
    [TestMethod]
    public void A11_Narrowing_Suggestions_Are_Limited_To_Four_Kinds()
    {
        var suggestionKinds = Enum.GetValues<RetrievalSuggestionKind>();
        Assert.HasCount(4, suggestionKinds, "收窄手段被冻结为**四类**（ADR-089 §2.2 C）");
        CollectionAssert.AreEquivalent(
            new[]
            {
                RetrievalSuggestionKind.DirectoryLimit,
                RetrievalSuggestionKind.FileTypeFilter,
                RetrievalSuggestionKind.CompositeFilter,
                RetrievalSuggestionKind.ExplicitIntent,
            },
            suggestionKinds);

        foreach (var kind in suggestionKinds)
        {
            var step = RetrievalNextStep.Narrow(kind, $"收窄：{kind}", "arg");
            Assert.AreEqual(RetrievalNextStepKind.NarrowQuery, step.Kind);
            Assert.AreEqual(kind, step.Narrowing, "收窄建议必须携带\"哪一类收窄\"");
            Assert.IsNull(step.Relaxing);
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.Text));
        }

        Assert.ThrowsExactly<ArgumentException>(
            () => RetrievalNextStep.Narrow(RetrievalSuggestionKind.ExplicitIntent, "   "),
            "建议文本不得为空（自由文本也得是文本）");

        var relax = RetrievalNextStep.Relax(RetrievalFacet.MatchTarget, "放宽匹配域：允许在声明/正文里匹配");
        Assert.AreEqual(RetrievalNextStepKind.RelaxFilter, relax.Kind);
        Assert.AreEqual(RetrievalFacet.MatchTarget, relax.Relaxing);
        Assert.IsNull(relax.Narrowing);
        Assert.IsNull(relax.Alternative);

        // 载荷与种类不一致的“万能建议”不可表示
        Assert.IsNull(RetrievalNextStep.RebuildIndex("重建索引").Alternative);
        Assert.IsNull(RetrievalNextStep.AcceptAbsence("确实不存在").Narrowing);
        Assert.ThrowsExactly<ArgumentException>(() => RetrievalNextStep.UseAlternativeFace("换面", " "));

        // 过载/截断场景下用"放宽过滤面"糊弄 ⇒ 拒绝
        var request = RetrievalTestData.Request();
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Create(
            request,
            [],
            0,
            RetrievalTestData.IndexVersion,
            emptyReason: RetrievalEmptyReason.GenuinelyAbsent,
            nextSteps: [relax]));
    }
}
