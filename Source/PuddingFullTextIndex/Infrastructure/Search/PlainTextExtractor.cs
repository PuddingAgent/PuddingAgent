using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Search;

/// <summary>
/// 纯文本文件提取器 — 直接读取文件内容。
/// 支持的扩展名由 FullTextIndexOptions.PlainTextExtensions 定义。
/// </summary>
public sealed class PlainTextExtractor : IFileContentExtractor
{
    public IReadOnlySet<string> SupportedExtensions { get; }

    public PlainTextExtractor()
        : this(new FullTextIndexOptions())
    {
    }

    public PlainTextExtractor(FullTextIndexOptions options)
    {
        SupportedExtensions = options.PlainTextExtensions;
    }

    public async Task<string> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        return await File.ReadAllTextAsync(filePath, ct);
    }
}
