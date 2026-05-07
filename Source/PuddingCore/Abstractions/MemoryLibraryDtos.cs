namespace PuddingCode.Abstractions;

/// <summary>图书馆记录。</summary>
public sealed record LibraryRecord(
    string LibraryId,
    string WorkspaceId,
    string Name,
    string? Description,
    long CreatedAt,
    long UpdatedAt);

/// <summary>书籍记录。</summary>
public sealed record BookRecord(
    string BookId,
    string LibraryId,
    string Title,
    string Summary,
    string Status,
    int Version,
    int AccessCount,
    long? LastAccessedAt,
    long CreatedAt,
    long UpdatedAt);

/// <summary>章节记录。</summary>
public sealed record ChapterRecord(
    string ChapterId,
    string BookId,
    string Title,
    int ChapterOrder,
    string Content,
    string ContentType,
    double Importance,
    string? SourceSessionId,
    int WordCount,
    long CreatedAt,
    long UpdatedAt);

/// <summary>指针记录。</summary>
public sealed record PointerRecord(
    string PointerId,
    string ChapterId,
    string TargetType,
    string TargetId,
    string? TargetLabel,
    string? Description,
    int Relevance,
    long CreatedAt);

/// <summary>分支记录。</summary>
public sealed record BranchRecord(
    string BranchId,
    string BookId,
    string BranchName,
    string? Description,
    string? CreatedBy,
    string? MergedInto,
    bool IsDefault,
    long CreatedAt);

/// <summary>经验包——LLM 交给记忆图书馆的最小输入单元。</summary>
public sealed record ExperiencePackage
{
    /// <summary>经验标题（如"MySQL 8.0 主从复制延迟排查"）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>正文（Markdown，含步骤/代码/结论）。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>建议的分类路径（如 ["技术/数据库/MySQL"]，可为空由图书馆自动推断）。</summary>
    public IReadOnlyList<string>? SuggestedTags { get; init; }

    /// <summary>来源会话 ID（用于回溯完整现场）。</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>重要性（0-1）。</summary>
    public double Importance { get; init; } = 0.5;

    /// <summary>关联的外部 URL（如 StackOverflow、文档）。</summary>
    public IReadOnlyList<string>? ExternalRefs { get; init; }
}

/// <summary>融合检索结果。</summary>
public sealed record RankedResult
{
    public string BookId { get; init; } = string.Empty;
    public string BookTitle { get; init; } = string.Empty;
    public string? ChapterId { get; init; }
    public string? ChapterTitle { get; init; }
    /// <summary>高亮摘要片段。</summary>
    public string Snippet { get; init; } = string.Empty;
    public double Score { get; init; }
    /// <summary>"tag" | "fts5" | "vector" | "pointer"</summary>
    public string MatchSource { get; init; } = string.Empty;
    /// <summary>是否触发了后台深度探索（本次查询结果可能需要下次查询补充）。</summary>
    public bool IsPendingDeepExplore { get; init; }
}

/// <summary>经验写入结果。</summary>
public sealed record ExperienceWriteResult(
    BookRecord Book,
    ChapterRecord Chapter);
