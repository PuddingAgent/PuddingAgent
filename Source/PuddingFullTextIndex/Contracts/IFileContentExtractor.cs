namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 文件内容提取器 — 从不同类型的文件中提取纯文本用于索引。
/// 每种文件格式一个实现：PlainTextExtractor, PdfPigExtractor, NpoiWordExtractor 等。
/// </summary>
public interface IFileContentExtractor
{
    /// <summary>该提取器支持的文件扩展名集合（含点，如 ".pdf"）。</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>从文件流中提取纯文本。</summary>
    /// <param name="filePath">文件绝对路径。</param>
    /// <param name="ct">取消令牌。</param>
    Task<string> ExtractAsync(string filePath, CancellationToken ct = default);
}
