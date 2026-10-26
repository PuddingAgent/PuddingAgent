namespace PuddingFinance.Documents;

/// <summary>一页的文本。<see cref="PageNumber"/> 是 PDF 里的原页码（从 1 开始）。</summary>
public sealed record PdfPageText(int PageNumber, string Text);

/// <summary>
/// 整份文档的抽取结果。
///
/// <para>
/// <see cref="TotalChars"/> 为 0 是**有信息量**的：巨潮的公告与年报绝大多数带文字层，
/// 抽不出字通常意味着扫描件（图片型 PDF），那时不做 OCR 就等于什么都没读到——
/// 这种情况必须显式暴露，而不是返回一份"看起来成功的空文档"。
/// </para>
/// </summary>
public sealed record PdfDocumentText(
    int PageCount,
    IReadOnlyList<PdfPageText> Pages,
    int TotalChars,
    int CjkChars)
{
    /// <summary>是否读到文字层。</summary>
    public bool HasExtractableText => TotalChars > 0;

    /// <summary>无文字层 ⇒ 多半是扫描件，需要 OCR 才能继续。</summary>
    public bool LooksLikeScan => TotalChars == 0;
}

/// <summary>
/// PDF 文本抽取。上层只依赖这个接口，不依赖任何第三方 PDF 库的类型。
/// </summary>
public interface IPdfTextSource
{
    /// <param name="pdfBytes">原始字节。**不要**传解码后的字符串——中文 PDF 会变成替换字符。</param>
    /// <param name="maxPages">只读前 N 页；null 表示全读。年报几百页，分析时常需要先取目录几页。</param>
    PdfDocumentText Extract(byte[] pdfBytes, int? maxPages = null);
}

/// <summary>PDF 无法解析。与"能解析但没有文字层"是两件事：前者该换文件，后者该上 OCR。</summary>
public sealed class PdfTextExtractionException(string message, Exception? inner = null)
    : Exception(message, inner);
