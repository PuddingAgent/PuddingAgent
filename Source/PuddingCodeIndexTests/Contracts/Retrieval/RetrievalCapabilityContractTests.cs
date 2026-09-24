using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A6：能力矩阵必须诚实（ADR-089 §4 / §5.2 / §8.3）。
/// <para>"不支持"必须**带替代方案**，绝不允许"假装支持"或"空即没有"。</para>
/// </summary>
[TestClass]
public sealed class RetrievalCapabilityContractTests
{
    /// <summary>A6：不支持 ⇒ 必须给替代方案；结果层同样不得把能力缺口报成真空。</summary>
    [TestMethod]
    public void A06_Capability_Matrix_Must_Say_Unsupported_With_Alternative()
    {
        // ① 类型层：不支持但不给替代方案 ⇒ 不可表示
        Assert.ThrowsExactly<ArgumentException>(
            () => LanguageCapability.Unsupported("go", RetrievalCapabilityFace.References, "   "),
            "\"不支持\"必须携带替代方案");
        Assert.ThrowsExactly<ArgumentException>(
            () => LanguageCapability.Supported("go", RetrievalCapabilityFace.References, RetrievalCapabilityLevel.None),
            "要声明\"不支持\"必须走 Unsupported（它强制替代方案）");

        var unsupported = LanguageCapability.Unsupported(
            "go",
            RetrievalCapabilityFace.References,
            "① 用文本面检索 SymbolName；② 用 Type intent 看是否有结构提取");
        Assert.IsFalse(unsupported.IsSupported);
        Assert.AreEqual(RetrievalCapabilityLevel.None, unsupported.Level);
        Assert.IsFalse(string.IsNullOrWhiteSpace(unsupported.Alternative));
        Assert.IsNull(unsupported.Note, "替代方案与备注不得混用（否则\"不支持必有替代\"会被备注稀释）");

        var supported = LanguageCapability.Supported(
            "csharp",
            RetrievalCapabilityFace.References,
            RetrievalCapabilityLevel.Semantic,
            "Roslyn 语义级");
        Assert.IsTrue(supported.IsSupported);
        Assert.IsNull(supported.Alternative, "支持的面上不得携带替代方案");
        Assert.AreEqual("Roslyn 语义级", supported.Note);
        Assert.AreEqual("csharp", supported.LanguageId, "语言标识必须规范化（小写）");

        // ② 端口形状冻结（本刀不实现矩阵数据，只冻结端口）
        var resolveMethod = typeof(ILanguageCapabilityMatrix).GetMethod("Resolve");
        Assert.IsNotNull(resolveMethod);
        Assert.AreEqual(typeof(LanguageCapability), resolveMethod!.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(string), typeof(RetrievalCapabilityFace) },
            resolveMethod.GetParameters().Select(p => p.ParameterType).ToArray());
        var describeMethod = typeof(ILanguageCapabilityMatrix).GetMethod("Describe");
        Assert.IsNotNull(describeMethod);
        Assert.AreEqual(typeof(IReadOnlyList<LanguageCapability>), describeMethod!.ReturnType);

        // ③ 替身实现：未知语言/不支持的面 ⇒ "不支持 + 替代方案"，而不是"空即没有"
        ILanguageCapabilityMatrix matrix = new StubCapabilityMatrix();
        var goReferences = matrix.Resolve("go", RetrievalCapabilityFace.References);
        Assert.IsFalse(goReferences.IsSupported);
        Assert.IsFalse(string.IsNullOrWhiteSpace(goReferences.Alternative));
        Assert.HasCount(Enum.GetValues<RetrievalCapabilityFace>().Length, matrix.Describe("go"));

        // ④ 结果层：能力缺口必须给"换面"建议（且不得报成真空/过泛）
        var request = RetrievalTestData.Request(intent: RetrievalIntent.References);
        var unsupportedResult = RetrievalResult.Empty(
            request,
            RetrievalEmptyReason.LanguageUnsupported,
            RetrievalNextStep.UseAlternativeFace(
                "语言 go 尚不支持 References",
                "① 用文本面检索 SymbolName；② 用 Type intent 看是否有结构提取"),
            RetrievalTestData.IndexVersion);

        Assert.AreEqual(RetrievalEmptyReason.LanguageUnsupported, unsupportedResult.EmptyReason);
        Assert.IsFalse(unsupportedResult.HasMore);
        Assert.HasCount(1, unsupportedResult.NextSteps);
        Assert.AreEqual(RetrievalNextStepKind.UseAlternativeFace, unsupportedResult.NextStep!.Kind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(unsupportedResult.NextStep.Alternative));

        // 反向对照：把能力缺口说成"查询太泛" ⇒ 拒绝
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Empty(
            request,
            RetrievalEmptyReason.LanguageUnsupported,
            RetrievalTestData.NarrowStep(),
            RetrievalTestData.IndexVersion));

        // 反向对照：换面建议却不给替代方案 ⇒ 类型层不可表示
        Assert.ThrowsExactly<ArgumentException>(
            () => RetrievalNextStep.UseAlternativeFace("go 尚不支持 References", "  "));
    }

    private sealed class StubCapabilityMatrix : ILanguageCapabilityMatrix
    {
        public LanguageCapability Resolve(string languageId, RetrievalCapabilityFace face) =>
            string.Equals(languageId, "csharp", StringComparison.OrdinalIgnoreCase)
                ? LanguageCapability.Supported(languageId, face, RetrievalCapabilityLevel.Semantic, "Roslyn")
                : LanguageCapability.Unsupported(languageId, face, "用文本面检索 SymbolName（结构面未实现）");

        public IReadOnlyList<LanguageCapability> Describe(string languageId) =>
            [.. Enum.GetValues<RetrievalCapabilityFace>().Select(face => Resolve(languageId, face))];
    }
}
