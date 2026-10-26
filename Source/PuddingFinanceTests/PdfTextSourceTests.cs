using System.Text;
using PuddingFinance.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PuddingFinanceTests;

/// <summary>
/// PDF 文本抽取的回归。
///
/// 刻意**不把真实年报/公告放进仓库**（一份 130 KB 样本会把叶子组件仓库变成文件仓库）。
/// 这里用 PdfPig 自己生成最小 PDF 验证"字节 → 页数 → 文本"这条通道；
/// 中文提取由 2026-10-26 的 live 探针兜底（巨潮工行公告，638 字符 / 329 汉字）。
/// </summary>
[TestClass]
public sealed class PdfTextSourceTests
{
    private readonly PdfPigTextSource _source = new();

    [TestMethod]
    public void Extract_GeneratedPdf_ReadsTextAndPageCount()
    {
        var bytes = BuildPdf(("Hello Pudding 2026", 1));

        var document = _source.Extract(bytes);

        Assert.AreEqual(1, document.PageCount);
        Assert.HasCount(1, document.Pages);
        Assert.IsTrue(document.HasExtractableText);
        Assert.IsFalse(document.LooksLikeScan);
        StringAssert.Contains(document.Pages[0].Text, "Hello");
        Assert.AreEqual(1, document.Pages[0].PageNumber, "页码应保持 PDF 内的原始编号");
    }

    [TestMethod]
    public void Extract_MaxPages_LimitsExtractionButKeepsTotalCount()
    {
        var bytes = BuildPdf(("Page one", 1), ("Page two", 2));

        var document = _source.Extract(bytes, maxPages: 1);

        Assert.AreEqual(2, document.PageCount, "PageCount 描述的是整份文档");
        Assert.HasCount(1, document.Pages, "maxPages 只限制抽多少页");
    }

    [TestMethod]
    public void Extract_EmptyBytes_Throws()
    {
        var error = Assert.ThrowsExactly<PdfTextExtractionException>(
            () => _source.Extract([]));

        StringAssert.Contains(error.Message, "为空");
    }

    [TestMethod]
    public void Extract_NonPdfBytes_ThrowsInsteadOfReturningEmptyDocument()
    {
        // 关键边界：坏文件必须抛错，不能回一份"0 字"的文档——
        // 否则调用方会把"打不开"误读成"这份公告没有文字层"。
        var error = Assert.ThrowsExactly<PdfTextExtractionException>(
            () => _source.Extract(Encoding.UTF8.GetBytes("this is not a pdf at all")));

        StringAssert.Contains(error.Message, "解析失败");
    }

    [TestMethod]
    public void Extract_ZeroMaxPages_Throws()
    {
        var bytes = BuildPdf(("x", 1));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _source.Extract(bytes, maxPages: 0));
    }

    [TestMethod]
    public void CountCjk_CountsHanOnly()
    {
        Assert.AreEqual(4, PdfPigTextSource.CountCjk("工商银行 ABC 123"));
        Assert.AreEqual(0, PdfPigTextSource.CountCjk("ABC 123"));
        Assert.AreEqual(0, PdfPigTextSource.CountCjk(null));
        Assert.AreEqual(1, PdfPigTextSource.CountCjk("㐀"), "扩展 A 区（U+3400–U+4DBF）也算汉字");
    }

    private static byte[] BuildPdf(params (string Text, int Page)[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var (text, _) in pages)
        {
            var page = builder.AddPage(595, 842);
            page.AddText(text, 12, new PdfPoint(25, 700), font);
        }

        return builder.Build();
    }
}
