using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PuddingFinance.Documents;

/// <summary>
/// 基于 PdfPig 的实现。**这是整个仓库里唯一引用 <c>UglyToad.PdfPig</c> 的文件**。
///
/// <para>
/// 选它的理由：Apache-2.0（无传染）、纯托管（无原生依赖）、维护活跃。
/// 代价是它仍是 0.x——README 明写 minor 版本会破坏公共 API，
/// 所以对外只暴露 <see cref="IPdfTextSource"/>，升级/换库时改动收敛在这里。
/// </para>
/// <para>
/// 实测（2026-10-26，巨潮工行公告 130 KB / 2 页）：中文完整可提取，无需 OCR。
/// </para>
/// </summary>
public sealed class PdfPigTextSource : IPdfTextSource
{
    public PdfDocumentText Extract(byte[] pdfBytes, int? maxPages = null)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        if (pdfBytes.Length == 0)
        {
            throw new PdfTextExtractionException("PDF 字节为空");
        }

        if (maxPages is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPages), maxPages, "maxPages 必须为正数或 null");
        }

        try
        {
            using var document = PdfDocument.Open(pdfBytes);
            var pages = new List<PdfPageText>();
            var totalChars = 0;
            var cjkChars = 0;

            foreach (var page in document.GetPages())
            {
                if (maxPages is { } limit && pages.Count >= limit)
                {
                    break;
                }

                // 不用 page.Text：那保留的是内容流顺序，不是人读的顺序。
                var text = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
                pages.Add(new PdfPageText(page.Number, text));
                totalChars += text.Length;
                cjkChars += CountCjk(text);
            }

            return new PdfDocumentText(document.NumberOfPages, pages, totalChars, cjkChars);
        }
        catch (Exception exception) when (exception is not PdfTextExtractionException)
        {
            throw new PdfTextExtractionException($"PDF 解析失败：{exception.Message}", exception);
        }
    }

    /// <summary>
    /// 统计汉字个数（基本区 + 扩展 A）。用来区分"读到中文"与"只读到数字和符号"。
    /// </summary>
    internal static int CountCjk(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if ((ch >= '\u4e00' && ch <= '\u9fff') || (ch >= '\u3400' && ch <= '\u4dbf'))
            {
                count++;
            }
        }

        return count;
    }
}
