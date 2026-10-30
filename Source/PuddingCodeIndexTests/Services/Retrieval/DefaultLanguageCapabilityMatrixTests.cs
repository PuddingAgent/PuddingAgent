using PuddingCodeIndex.Contracts.Retrieval;
using PuddingCodeIndex.Services.Retrieval;

namespace PuddingCodeIndexTests.Services.Retrieval;

/// <summary>
/// 能力矩阵的诚实性。每条都在守「不许假装支持」：
/// 不支持的必须给替代方案，未知语言不能返回"支持但空"。
/// </summary>
[TestClass]
public sealed class DefaultLanguageCapabilityMatrixTests
{
    private readonly DefaultLanguageCapabilityMatrix _matrix = new();

    [TestMethod]
    public void EveryUnsupportedEntry_CarriesAnAlternative()
    {
        // 这条是整个矩阵的地基：Unsupported 不给替代 = 让 Agent 在死路上空转。
        var languages = DefaultLanguageCapabilityMatrix.DeclaredLanguages
            .Concat(["go", "rust", "java", ""])
            .Distinct(StringComparer.Ordinal);

        foreach (var language in languages)
        {
            foreach (var capability in _matrix.Describe(language))
            {
                if (capability.IsSupported)
                {
                    Assert.IsNull(capability.Alternative,
                        $"{capability.LanguageId}/{capability.Face} 声明支持时不应带替代方案。");
                }
                else
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(capability.Alternative),
                        $"{capability.LanguageId}/{capability.Face} 声明不支持时必须给替代方案。");
                }
            }
        }
    }

    [TestMethod]
    public void UnknownLanguage_NeverReportsSupported()
    {
        // 未知语言返回 Supported(None) 会被读成"支持但结果为空" —— 必须返回 Unsupported。
        foreach (var face in Enum.GetValues<RetrievalCapabilityFace>())
        {
            var capability = _matrix.Resolve("klingon", face);

            Assert.IsFalse(capability.IsSupported, $"未知语言在 {face} 上不得声明支持。");
            StringAssert.Contains(capability.Alternative!, "search_grep");
        }
    }

    [TestMethod]
    public void EmptyLanguageId_IsTreatedAsUnknown()
    {
        var capability = _matrix.Resolve("  ", RetrievalCapabilityFace.SymbolIndexing);

        Assert.IsFalse(capability.IsSupported);
        Assert.AreEqual("unknown", capability.LanguageId);
    }

    [TestMethod]
    public void LanguageId_IsNormalizedToLowerCase()
    {
        var capitalized = _matrix.Resolve("CSharp", RetrievalCapabilityFace.RelationGraph);
        var lowercase = _matrix.Resolve("csharp", RetrievalCapabilityFace.RelationGraph);

        Assert.IsTrue(capitalized.IsSupported);
        Assert.AreEqual(RetrievalCapabilityLevel.Semantic, capitalized.Level);
        Assert.AreEqual(lowercase.Level, capitalized.Level);
    }

    [TestMethod]
    public void CSharp_DeclaresSemanticOnTheRoslynBackedFaces()
    {
        var faces = new[]
        {
            RetrievalCapabilityFace.SymbolExtraction,
            RetrievalCapabilityFace.SymbolIndexing,
            RetrievalCapabilityFace.RelationGraph,
            RetrievalCapabilityFace.References,
        };

        foreach (var face in faces)
        {
            var capability = _matrix.Resolve(DefaultLanguageCapabilityMatrix.CSharp, face);
            Assert.AreEqual(RetrievalCapabilityLevel.Semantic, capability.Level,
                $"C# 在 {face} 上应由 Roslyn 支撑到 Semantic 级。");
        }
    }

    [TestMethod]
    public void CSharp_DocComments_IsDeclaredUnsupported_BecauseRetrievalIsBroken()
    {
        // 索引器能产注释，但检索侧 scope 映射错位（R1-A）导致暂时查不到。
        // 对 Agent 而言"能不能查到"才是能力 —— 所以这里必须是不支持。
        var capability = _matrix.Resolve(DefaultLanguageCapabilityMatrix.CSharp, RetrievalCapabilityFace.DocComments);

        Assert.IsFalse(capability.IsSupported,
            "注释面当前不可检索；若声明支持，Agent 会反复在注释面空转。");
        StringAssert.Contains(capability.Alternative!, "search_grep");
    }

    [TestMethod]
    public void TypeScript_NeverClaimsSemantic()
    {
        // TS 只有 outliner 级提取，没有静态绑定；声明 Semantic 就是谎。
        foreach (var capability in _matrix.Describe(DefaultLanguageCapabilityMatrix.TypeScript))
            Assert.AreNotEqual(RetrievalCapabilityLevel.Semantic, capability.Level,
                $"TypeScript 在 {capability.Face} 上不得声明 Semantic。");
    }

    [TestMethod]
    public void Describe_CoversEveryFace()
    {
        var described = _matrix.Describe(DefaultLanguageCapabilityMatrix.CSharp);

        Assert.AreEqual(Enum.GetValues<RetrievalCapabilityFace>().Length, described.Count,
            "Describe 必须覆盖全部能力面，否则覆盖矩阵会漏格。");
    }

    [TestMethod]
    public void DeclaredLanguages_AreNotDerivedFromRequestTimeInput()
    {
        // 矩阵是声明物的清单，不是"查过什么"的记录 —— 它必须稳定。
        CollectionAssert.AreEqual(
            new[] { "csharp", "markdown", "typescript" },
            DefaultLanguageCapabilityMatrix.DeclaredLanguages.ToArray());
    }
}
