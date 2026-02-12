using System.ComponentModel.DataAnnotations;

namespace PuddingCode.Abstractions;

/// <summary>
/// 记忆图书馆底层接口——提供精确的 CRUD 与检索能力。
/// 类比关系数据库的 SQL 层，不含业务策略。
/// </summary>
public interface IMemoryLibrary
{
    // ── Library 生命周期 ──

    /// <summary>创建图书馆。</summary>
    Task<LibraryRecord> CreateLibraryAsync(string workspaceId, string name, string? description, CancellationToken ct = default);

    /// <summary>获取单个图书馆。</summary>
    Task<LibraryRecord?> GetLibraryAsync(string libraryId, CancellationToken ct = default);

    /// <summary>列出 Workspace 下所有图书馆。</summary>
    Task<IReadOnlyList<LibraryRecord>> ListLibrariesAsync(string workspaceId, CancellationToken ct = default);

    // ── Book 生命周期 ──

    /// <summary>创建书籍，可选 Tag 路径。</summary>
    Task<BookRecord> CreateBookAsync(
        string libraryId, string title, string summary,
        IReadOnlyList<string>? tagPaths = null, CancellationToken ct = default);

    /// <summary>获取单本书。</summary>
    Task<BookRecord?> GetBookAsync(string bookId, CancellationToken ct = default);

    /// <summary>获取单本书（只读，不更新 AccessCount，适用于批量检索场景）。</summary>
    Task<BookRecord?> GetBookReadOnlyAsync(string bookId, CancellationToken ct = default);

    /// <summary>更新书籍字段（通过回调返回新 record）。</summary>
    Task<BookRecord> UpdateBookAsync(string bookId, Func<BookRecord, BookRecord> updater, CancellationToken ct = default);

    /// <summary>归档书籍。</summary>
    Task<bool> ArchiveBookAsync(string bookId, CancellationToken ct = default);

    /// <summary>列出图书馆下的书籍。</summary>
    Task<IReadOnlyList<BookRecord>> ListBooksAsync(string libraryId, int limit = 50, CancellationToken ct = default);

    // ── Chapter 生命周期 ──

    /// <summary>为书籍添加章节。ADR-042: agentInstanceId 标记 Agent 私有章节（null=共享）。</summary>
    Task<ChapterRecord> AddChapterAsync(
        string bookId, string title, string content,
        int chapterOrder = 0, string? sourceSessionId = null,
        string? agentInstanceId = null,
        CancellationToken ct = default);

    /// <summary>为书籍添加章节（含来源引用）。ADR-028 Phase 1。</summary>
    Task<ChapterRecord> AddChapterWithSourceAsync(
        string bookId, string title, string content,
        int chapterOrder = 0, string? sourceSessionId = null,
        string? sourceReference = null, string? referenceType = null,
        CancellationToken ct = default);

    /// <summary>获取单个章节。</summary>
    Task<ChapterRecord?> GetChapterAsync(string chapterId, CancellationToken ct = default);

    /// <summary>更新章节内容（同时更新 WordCount、UpdatedAt、触发 Book Version++）。</summary>
        Task<ChapterRecord> UpdateChapterContentAsync(string chapterId, string newContent, CancellationToken ct = default);

    /// <summary>更新章节标题（同时更新 TitleTokens、UpdatedAt、触发 Book Version++）。</summary>
    Task<ChapterRecord> UpdateChapterTitleAsync(string chapterId, string newTitle, CancellationToken ct = default);

    /// <summary>更新章节重要性（0-1）。</summary>
    Task<ChapterRecord> UpdateChapterImportanceAsync(string chapterId, double importance, CancellationToken ct = default);

    /// <summary>创建章节的新 active 版本，并将旧章节标记为 superseded。</summary>
    Task<ChapterRecord> SupersedeChapterAsync(
        string chapterId, string newTitle, string newContent,
        string? sourceSessionId = null, string? agentInstanceId = null,
        CancellationToken ct = default);

    /// <summary>列出书籍的 active 章节（按 ChapterOrder 排序）。旧版本通过 GetChapterAsync 精确审计。</summary>
    Task<IReadOnlyList<ChapterRecord>> ListChaptersAsync(string bookId, CancellationToken ct = default);

    /// <summary>列出书籍的 active 与 superseded 章节，用于显式历史审计。</summary>
    Task<IReadOnlyList<ChapterRecord>> ListChapterHistoryAsync(string bookId, CancellationToken ct = default);

    // ── Pointer ──

    /// <summary>创建指针引用（旧兼容 API，内部双写 SourceType/SourceId）。</summary>
    Task<PointerRecord> CreatePointerAsync(
        string chapterId, string targetType, string targetId,
        string? label = null, string? description = null, CancellationToken ct = default);

    /// <summary>创建泛化指针——任意来源到任意目标。ADR-029。</summary>
    Task<PointerRecord> CreateGeneralPointerAsync(
        string workspaceId, string sourceType, string sourceId,
        string targetType, string targetId,
        string? label = null, string? description = null, CancellationToken ct = default);

    /// <summary>获取章节的所有指针（旧兼容 API）。</summary>
    Task<IReadOnlyList<PointerRecord>> GetPointersAsync(string chapterId, CancellationToken ct = default);

    /// <summary>按 workspace + 来源类型/ID 查询指针。ADR-029。</summary>
    Task<IReadOnlyList<PointerRecord>> GetPointersBySourceAsync(
        string workspaceId, string sourceType, string sourceId, CancellationToken ct = default);

    /// <summary>反向查询：哪些指针指向了指定的目标。</summary>
    Task<IReadOnlyList<PointerRecord>> ResolveBacklinksAsync(string targetType, string targetId, CancellationToken ct = default);

    /// <summary>按 Title 精确查找书籍（用于去重），不更新 AccessCount。</summary>
    Task<BookRecord?> FindBookByTitleAsync(string libraryId, string title, CancellationToken ct = default);

    // ── 删除操作 ──

    /// <summary>删除图书馆（级联删除所有 Book/Chapter/Pointer）。</summary>
    Task<bool> DeleteLibraryAsync(string libraryId, CancellationToken ct = default);

    /// <summary>删除书籍（级联删除所有 Chapter/Pointer/BookIndex）。</summary>
    Task<bool> DeleteBookAsync(string bookId, CancellationToken ct = default);

    /// <summary>删除章节（级联删除所有 Pointer）。</summary>
    Task<bool> DeleteChapterAsync(string chapterId, CancellationToken ct = default);

    /// <summary>删除指针引用。</summary>
    Task<bool> DeletePointerAsync(string pointerId, CancellationToken ct = default);

    // ── 检索 ──

    /// <summary>FTS5 全文搜索书籍（BM25 排序，返回 RankedResult 含归一化分数）。</summary>
    Task<IReadOnlyList<RankedResult>> SearchBooksFtsScoredAsync(string query, int topK = 20, CancellationToken ct = default);

    /// <summary>FTS5 全文搜索章节（BM25 排序，返回 RankedResult 含归一化分数）。</summary>
    Task<IReadOnlyList<RankedResult>> SearchChaptersFtsScoredAsync(string query, int topK = 20, CancellationToken ct = default);

    /// <summary>FTS5 全文搜索书籍（仅返回 BookRecord，不含分数——保留向后兼容）。</summary>
    Task<IReadOnlyList<BookRecord>> SearchBooksFtsAsync(string query, int topK = 20, CancellationToken ct = default);

    /// <summary>FTS5 全文搜索章节（仅返回 ChapterRecord，不含分数——保留向后兼容）。</summary>
    Task<IReadOnlyList<ChapterRecord>> SearchChaptersFtsAsync(string query, int topK = 20, CancellationToken ct = default);

    /// <summary>FTS5 全文搜索章节（workspace scoped + AgentInstanceId 过滤）。ADR-028 Phase 1 / ADR-042 Agent 记忆隔离。</summary>
    Task<IReadOnlyList<RankedResult>> SearchChaptersFtsScopedAsync(
        string workspaceId, string query, int topK = 20,
        CancellationToken ct = default,
        string? agentInstanceId = null,
        bool includeHistory = false);

    /// <summary>列出 Workspace 下所有图书馆的书籍。ADR-028 Phase 1。</summary>
    Task<IReadOnlyList<BookRecord>> ListBooksScopedAsync(
        string workspaceId, int limit = 50, CancellationToken ct = default);

    // ── ADR-028 Phase 2: SourceReference ──

    /// <summary>创建来源引用记录。</summary>
    Task<SourceReferenceRecord> AddSourceReferenceAsync(
        SourceReferenceCreateRequest request, CancellationToken ct = default);

    /// <summary>获取指定 owner 的所有来源引用。</summary>
    Task<IReadOnlyList<SourceReferenceRecord>> GetSourceReferencesAsync(
        string ownerType, string ownerId, CancellationToken ct = default);

    // ── ADR-028 Phase 3: TreeNode ──

    /// <summary>获取树节点子节点。</summary>
    Task<IReadOnlyList<TreeNodeRecord>> GetTreeChildrenAsync(
        string workspaceId, string libraryId, string? parentNodeId,
        CancellationToken ct = default);

    /// <summary>创建树节点。</summary>
    Task<TreeNodeRecord> CreateTreeNodeAsync(
        string workspaceId, string libraryId, string? parentNodeId,
        string name, string? summary = null, string nodeType = "category",
        CancellationToken ct = default);

    /// <summary>将 Book 挂载到 TreeNode。</summary>
    Task<BookTreeMountRecord> MountBookAsync(
        string bookId, string nodeId, int weight = 1,
        CancellationToken ct = default);

    /// <summary>确保默认系统 Books 存在（懒创建）。</summary>
    Task EnsureDefaultBooksAsync(
        string workspaceId, string libraryId,
        CancellationToken ct = default);

    /// <summary>
    /// 回填存量 Chapter 的 TitleTokens / ContentTokens 分词列。
    /// 幂等：只处理 ContentTokens 为空的记录。
    /// </summary>
    Task BackfillTokensAsync(CancellationToken ct = default);

    /// <summary>按 Tag 前缀搜索书籍。</summary>
    Task<IReadOnlyList<BookRecord>> SearchBooksByTagAsync(string tagPrefix, int topK = 20, CancellationToken ct = default);

    /// <summary>获取 Tag 树子节点。</summary>
    Task<IReadOnlyList<TagTreeNode>> GetTagChildrenAsync(string? parentTag = null, CancellationToken ct = default);

    // ── Phase 4: 向量检索 ──

    /// <summary>嵌入向量搜索章节（余弦相似度）。仅搜索有 Embedding 的 Chapter。</summary>
    Task<IReadOnlyList<RankedResult>> SearchChaptersByVectorAsync(float[] queryEmbedding, int topK = 20, CancellationToken ct = default);

    /// <summary>融合检索：FTS5 + TagTree + Vector，RRF 合并排名。</summary>
    Task<IReadOnlyList<RankedResult>> HybridSearchAsync(string query, float[]? queryEmbedding, int topK = 20, CancellationToken ct = default);

    /// <summary>更新章节的嵌入向量。</summary>
    Task<bool> UpdateChapterEmbeddingAsync(string chapterId, byte[] embedding, CancellationToken ct = default);

    // ── 分支 ──

    /// <summary>从 Book 创建新分支。</summary>
    Task<BranchRecord> BranchBookAsync(string bookId, string branchName, string? description, CancellationToken ct = default);

    /// <summary>列出 Book 的所有分支。</summary>
    Task<IReadOnlyList<BranchRecord>> ListBranchesAsync(string bookId, CancellationToken ct = default);

    /// <summary>将源分支合并到目标分支。</summary>
    Task<bool> MergeBranchAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default);

    // ── Phase 1: Chapter 关联关系（知识图谱边）──

    /// <summary>创建章节关联关系。</summary>
    Task<ChapterRelationRecord> CreateChapterRelationAsync(
        string sourceChapterId, string targetChapterId,
        string relationType, string? description = null,
        double weight = 1.0, CancellationToken ct = default);

    /// <summary>获取章节的所有关联关系（作为源或目标）。</summary>
    Task<IReadOnlyList<ChapterRelationRecord>> GetChapterRelationsAsync(
        string chapterId, string? relationType = null, CancellationToken ct = default);

    /// <summary>图遍历：获取章节的多度关联。</summary>
    Task<IReadOnlyList<ChapterRelationRecord>> GetRelatedChaptersAsync(
        string chapterId, int depth = 1, double minWeight = 0.0, CancellationToken ct = default);

    /// <summary>更新章节的元数据字段（Scene, Constraints, Tags）。仅更新非 null 的字段。</summary>
        Task<ChapterRecord> UpdateChapterMetadataAsync(
        string chapterId, string? scene = null, string? constraints = null,
        string? tags = null, CancellationToken ct = default);

    // ── Phase 2: Book 去重 ──

    /// <summary>
    /// 将源 Book 的所有章节移动到目标 Book，然后删除源 Book。
    /// 用于 Book 去重合并。返回移动的章节数。
    /// </summary>
    Task<int> MergeBookChaptersAsync(string sourceBookId, string targetBookId, CancellationToken ct = default);
}
