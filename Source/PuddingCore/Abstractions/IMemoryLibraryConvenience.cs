namespace PuddingCode.Abstractions;

/// <summary>
/// LLM 友好操作层——提供"直接丢内容进来，图书馆自己管路由"的便利方法。
/// 这些方法不包含业务策略，只是存储引擎内部的智能路由。
/// </summary>
public interface IMemoryLibraryConvenience
{
    // ── 经验写入（Upsert 模式）──

    /// <summary>
    /// 写入一段经验。内部自动：
    /// 1. FTS5 搜索相似 Book → 有则追加 Chapter，无则新建 Book
    /// 2. 自动计算 ChapterOrder（追加到末尾）
    /// 3. 自动发现 Content 中引用其他 Book/Chapter 的关键词并创建 Pointer
    /// </summary>
    Task<ExperienceWriteResult> UpsertExperienceAsync(
        string workspaceId, ExperiencePackage experience,
        CancellationToken ct = default);

    // ── 智能检索（一次调用融合多路）──

    /// <summary>融合 TagTree + FTS5 的自然语言检索（Phase 4 加入向量）。</summary>
    Task<IReadOnlyList<RankedResult>> SmartSearchAsync(
        string naturalLanguageQuery,
        int topK = 20,
        CancellationToken ct = default);

    // ── 便捷操作 ──

    /// <summary>按 Title 精确获取或创建 Book（Title 作为自然键去重）。</summary>
    Task<BookRecord> GetOrCreateBookAsync(
        string libraryId, string title, string? summary, IReadOnlyList<string>? tagPaths, CancellationToken ct);

    /// <summary>追加 Chapter 到 Book 末尾（自动计算 ChapterOrder）。</summary>
    Task<ChapterRecord> AppendChapterAsync(
        string bookId, string title, string content, string? sourceSessionId = null, CancellationToken ct = default);

    /// <summary>扫描 Content 中的已知 Book/Chapter 引用，自动创建 Pointer。</summary>
    Task<IReadOnlyList<PointerRecord>> AutoDiscoverPointersAsync(
        string chapterId, CancellationToken ct = default);

    /// <summary>获取 Tag 树根节点（"图书馆→技术→数据库→MySQL" 的顶层分类）。</summary>
    Task<IReadOnlyList<TagTreeNode>> GetTagRootsAsync(CancellationToken ct = default);
}
