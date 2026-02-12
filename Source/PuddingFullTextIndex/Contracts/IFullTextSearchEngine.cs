namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 全文索引搜索与索引管理。提供 hasIndex / search / build / rebuild 能力。
/// 由 LuceneSearchEngine 实现。
/// </summary>
public interface IFullTextSearchEngine
{
    /// <summary>检查指定目录是否已经建立索引。</summary>
    bool HasIndex(string directoryPath);

    /// <summary>
    /// 全文搜索，返回匹配的文件路径和命中行。
    /// </summary>
    /// <param name="query">搜索关键词。</param>
    /// <param name="directoryPath">限定的目录范围。</param>
    /// <param name="maxResults">返回最大匹配数，默认 30。</param>
    /// <param name="fileExtensionFilter">可选的扩展名白名单（如 ".cs;.ts"）；null 或空表示不过滤。</param>
    /// <param name="subDirectoryFilter">可选的子目录前缀过滤（如 "Source/PuddingRuntime"）；null 或空表示不过滤。</param>
    /// <param name="ct">取消令牌。</param>
    Task<FullTextSearchResult> SearchAsync(
        string query,
        string directoryPath,
        int maxResults = 30,
        string? fileExtensionFilter = null,
        string? subDirectoryFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// 构建或更新目录的全文索引。
    /// </summary>
    /// <param name="directoryPath">目标目录。</param>
    /// <param name="filePatterns">文件名 glob 模式（如 "*.cs;*.md"）；null 使用默认白名单。</param>
    /// <param name="ct">取消令牌。</param>
    Task<FullTextIndexResult> BuildIndexAsync(
        string directoryPath,
        string? filePatterns = null,
        CancellationToken ct = default);

    /// <summary>删除目录的索引。</summary>
    bool RemoveIndex(string directoryPath);
}

/// <summary>全文检索结果。</summary>
public sealed record FullTextSearchResult(
    bool Success,
    IReadOnlyList<FullTextSearchMatch> Matches,
    string? Error,
    int TotalMatches,
    long ElapsedMs);

/// <summary>单条匹配记录。</summary>
public sealed record FullTextSearchMatch(
    string FilePath,
    int LineNumber,
    string LineText);

/// <summary>索引构建/更新结果。</summary>
public sealed record FullTextIndexResult(
    bool Success,
    int IndexedFileCount,
    long TotalBytes,
    long ElapsedMs,
    string? Error);
