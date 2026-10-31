using PuddingRetrieval;
using PuddingRetrieval.Contracts;

namespace PuddingRetrievalTests;

/// <summary>
/// 覆盖矩阵：把「能力事实」与「项目里有哪些语言」在输出层交叉。
/// 守三件事：语言必去重归一、未声明语言不静默、Markdown 不留白。
/// </summary>
[TestClass]
public sealed class RetrievalCoverageMatrixTests
{
    private readonly DefaultLanguageCapabilityMatrix _matrix = new();

    private static readonly int FaceCount = Enum.GetValues<RetrievalCapabilityFace>().Length;

    [TestMethod]
    public void Build_ProducesOneCellPerLanguageAndFace()
    {
        var rows = RetrievalCoverageMatrix.Build(
            [new ProjectLanguages("proj-a", ["csharp", "typescript"])],
            _matrix);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(2 * FaceCount, rows[0].Cells.Count);
    }

    [TestMethod]
    public void Build_NormalizesAndDeduplicatesLanguages()
    {
        var rows = RetrievalCoverageMatrix.Build(
            [new ProjectLanguages("proj-a", ["CSharp", " csharp ", "CSHARP", ""])],
            _matrix);

        var languages = rows[0].Cells.Select(cell => cell.LanguageId).Distinct().ToArray();

        CollectionAssert.AreEqual(new[] { "csharp" }, languages);
        Assert.AreEqual(FaceCount, rows[0].Cells.Count);
    }

    [TestMethod]
    public void Build_UndeclaredLanguage_IsUnsupportedEverywhere_NotSilentlyDropped()
    {
        // 语言不在矩阵里时不能悄悄消失 —— 那会让覆盖表看起来"这个项目什么都支持"（因为只剩支持的格子）。
        var rows = RetrievalCoverageMatrix.Build(
            [new ProjectLanguages("proj-a", ["go"])],
            _matrix);

        Assert.AreEqual(FaceCount, rows[0].Cells.Count, "未声明的语言仍要占满一整行。");
        Assert.IsTrue(rows[0].Cells.All(cell => !cell.Supported));
        Assert.IsTrue(rows[0].Cells.All(cell => !string.IsNullOrWhiteSpace(cell.Alternative)));
        Assert.AreEqual(0, rows[0].SupportedCellCount);
    }

    [TestMethod]
    public void Build_SkipsProjectsWithoutId()
    {
        var rows = RetrievalCoverageMatrix.Build(
            [new ProjectLanguages("  ", ["csharp"]), new ProjectLanguages("proj-b", ["csharp"])],
            _matrix);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("proj-b", rows[0].ProjectId);
    }

    [TestMethod]
    public void Build_EmptyInput_YieldsNoRows()
    {
        Assert.AreEqual(0, RetrievalCoverageMatrix.Build([], _matrix).Count);
    }

    [TestMethod]
    public void SupportedLanguages_OnlyCountsLanguagesWithAtLeastOneSupportedFace()
    {
        var rows = RetrievalCoverageMatrix.Build(
            [new ProjectLanguages("proj-a", ["csharp", "go"])],
            _matrix);

        CollectionAssert.AreEqual(new[] { "csharp" }, rows[0].SupportedLanguages.ToArray());
    }

    [TestMethod]
    public void ToMarkdown_HasAHeaderPerFace_AndDashForMissingCells()
    {
        var rows = RetrievalCoverageMatrix.Build(
            [new ProjectLanguages("proj-a", ["csharp"])],
            _matrix);

        var markdown = RetrievalCoverageMatrix.ToMarkdown(rows[0]);

        foreach (var face in Enum.GetValues<RetrievalCapabilityFace>())
            StringAssert.Contains(markdown, face.ToString());

        StringAssert.Contains(markdown, "| csharp |");
        StringAssert.Contains(markdown, "Semantic");
        StringAssert.Contains(markdown, "None");
        Assert.IsFalse(markdown.Contains("||", StringComparison.Ordinal), "空单元格不能被渲染成空字符串。");
    }

    [TestMethod]
    public void Build_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RetrievalCoverageMatrix.Build(null!, _matrix));
        Assert.Throws<ArgumentNullException>(() =>
            RetrievalCoverageMatrix.Build([], null!));
    }
}
