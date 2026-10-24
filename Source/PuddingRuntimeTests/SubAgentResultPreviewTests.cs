using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests;

/// <summary>
/// 子代理结果预览的两条不变量：短结果原样返回（不得改写），超长结果必须截断并留下总长标记
/// （否则读者会以为结果只到这里）。
/// </summary>
[TestClass]
public sealed class SubAgentResultPreviewTests
{
    [TestMethod]
    public void Condense_ReturnsShortResultUnchanged()
    {
        var text = "SUMMARY: done";

        Assert.AreEqual(text, SubAgentResultPreview.Condense(text));
    }

    [TestMethod]
    public void Condense_TruncatesLongResultAndMarksTotalLength()
    {
        var text = new string('x', SubAgentResultPreview.MaxPreviewChars + 5000);

        var condensed = SubAgentResultPreview.Condense(text);

        Assert.IsTrue(condensed.Length < text.Length, "必须真的截断");
        Assert.IsTrue(condensed.StartsWith(new string('x', 100)), "头部必须原样保留");
        Assert.IsTrue(condensed.Contains("结果已截断"), "必须显式标注被截断");
        Assert.IsTrue(condensed.Contains((text.Length).ToString("N0")), "必须标注原始总长");
    }

    [TestMethod]
    public void Condense_HandlesNullAndEmpty()
    {
        Assert.AreEqual(string.Empty, SubAgentResultPreview.Condense(null));
        Assert.AreEqual(string.Empty, SubAgentResultPreview.Condense(string.Empty));
    }
}
