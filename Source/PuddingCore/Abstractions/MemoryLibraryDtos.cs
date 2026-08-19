namespace PuddingCode.Abstractions;

/// <summary>图书馆记录。</summary>
public sealed record LibraryRecord(
    string LibraryId,
    string WorkspaceId,
    string Name,
    string? Description,
    long CreatedAt,
    long UpdatedAt,
    string? AgentId = null);

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
    long UpdatedAt,
    string? SourceReference = null,
    string? ReferenceType = null,
    string? AgentInstanceId = null,
    string? Scene = null,
    string? Constraints = null,
    string? Tags = null,
    string Status = "active",
    string? SupersededByChapterId = null,
    long? SupersededAt = null);

/// <summary>指针记录。ADR-029: 泛化为通用指针。</summary>
public sealed record PointerRecord(
    string PointerId,
    string ChapterId,
    string TargetType,
    string TargetId,
    string? TargetLabel,
    string? Description,
    int Relevance,
    long CreatedAt,
    string? WorkspaceId = null,
    string? SourceType = null,
    string? SourceId = null);

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

    /// <summary>来源引用指针字符串（如 session:abc123、url:https://...）。</summary>
    public string? SourceReference { get; init; }

    /// <summary>引用类型：none | session | session_event | session_slice | url | file | memo。</summary>
    public string? ReferenceType { get; init; }

    /// <summary>重要性（0-1）。</summary>
    public double Importance { get; init; } = 0.5;

    /// <summary>关联的外部 URL（如 StackOverflow、文档）。</summary>
    public IReadOnlyList<string>? ExternalRefs { get; init; }

    /// <summary>Agent 实例 ID（null = 共享知识）。ADR-042 Agent 记忆隔离。</summary>
    public string? AgentInstanceId { get; init; }
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
    /// <summary>命中章节状态：active | superseded。</summary>
    public string Status { get; init; } = "active";
    /// <summary>当 Status=superseded 时，指向取代它的新章节。</summary>
    public string? SupersededByChapterId { get; init; }
    /// <summary>被取代时间（Unix 时间戳，毫秒）。</summary>
    public long? SupersededAt { get; init; }
}

/// <summary>经验写入结果。</summary>
public sealed record ExperienceWriteResult(
    BookRecord Book,
    ChapterRecord Chapter);

/// <summary>
/// 会话块向量召回结果（WP-L2c 第 5 路专用 DTO，P1-2 T3）。
/// 不复用 <see cref="RankedResult"/>：避免污染 Library 语义与既有序列化契约（P1-2 方案风险6）。
/// 携带 MessageId / CanonicalContentHash / ContextGeneration 供上层同源去重（T4 透传至 RecalledMemory）。
/// </summary>
public sealed record SessionChunkRankedResult
{
    /// <summary>会话块 ID（SessionChunkVectors.ChunkId）。</summary>
    public string ChunkId { get; init; } = "";

    /// <summary>所属会话 ID。</summary>
    public string SessionId { get; init; } = "";

    /// <summary>源消息 ID（断点②修复：投影透传，供上层溯源/去重）。</summary>
    public string MessageId { get; init; } = "";

    /// <summary>块文本（完整原文，供摘要与 hash 校验）。</summary>
    public string SourceText { get; init; } = "";

    /// <summary>嵌入向量（float32 字节数组）。</summary>
    public byte[]? Embedding { get; init; }

    /// <summary>内容规范化 SHA-256（小写 hex）。优先写侧冗余列 → 联表 Messages 值 → SourceText 现算兜底。</summary>
    public string? CanonicalContentHash { get; init; }

    /// <summary>该块所属压缩代际（与 MessageEntity.ContextGeneration 对齐；未知为 null）。</summary>
    public int? ContextGeneration { get; init; }

    /// <summary>余弦相似度（保留 4 位小数）。</summary>
    public double Score { get; init; }

    /// <summary>源消息是否已被压缩覆盖（Messages.CompactedBy != null）。</summary>
    public bool IsCovered { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// ADR-028 Phase 1-3: 新增 DTO
// ═══════════════════════════════════════════════════════════════

/// <summary>来源引用记录——一等溯源数据结构。</summary>
public sealed record SourceReferenceRecord(
    string SourceReferenceId,
    string WorkspaceId,
    string OwnerType,
    string OwnerId,
    string TargetType,
    string TargetId,
    string? TargetRange,
    string? Label,
    string? Description,
    long CreatedAt);

/// <summary>来源引用创建请求。</summary>
public sealed record SourceReferenceCreateRequest(
    string WorkspaceId,
    string OwnerType,
    string OwnerId,
    string TargetType,
    string TargetId,
    string? TargetRange = null,
    string? Label = null,
    string? Description = null);

/// <summary>来源引用解析状态。</summary>
public enum SourceResolveStatus
{
    Resolved,
    Missing,
    Unauthorized,
    Unsupported
}

/// <summary>来源引用摘要（轻量，用于 UI 和 prompt 注入）。</summary>
public sealed record SourceReferenceSummary(
    string TargetType,
    string TargetId,
    string? Label,
    SourceResolveStatus ResolveStatus);

/// <summary>树节点记录。</summary>
public sealed record TreeNodeRecord(
    string NodeId,
    string WorkspaceId,
    string LibraryId,
    string? ParentNodeId,
    string Path,
    string Name,
    string? Summary,
    string NodeType,
    string Status,
    int SortOrder,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Book 挂载到 TreeNode 的记录。</summary>
public sealed record BookTreeMountRecord(
    string Id,
    string BookId,
    string NodeId,
    int Weight,
    long CreatedAt);

/// <summary>记忆摄入请求（Librarian 入口）。</summary>
public sealed record MemoryIngestionRequest(
    string WorkspaceId,
    string LibraryId,
    ExperiencePackage Experience,
    string? TargetBookTitle = null,
    string? TargetChapterId = null);

/// <summary>记忆树操作类型。</summary>
public enum MemoryTreeOperationType
{
    CreateNode,
    MountBook,
    MoveBook,
    RenameNode,
    MergeNode,
    ArchiveNode,
    AddPointer
}

/// <summary>记忆树操作（Librarian 输出，Core 执行）。</summary>
public sealed record MemoryTreeOperation(
    MemoryTreeOperationType OperationType,
    string WorkspaceId,
    string LibraryId,
    string? NodeId = null,
    string? ParentNodeId = null,
    string? Name = null,
    string? BookId = null,
    int Weight = 1);

/// <summary>章节关联关系记录。Phase 1: 知识图谱边。</summary>
public sealed record ChapterRelationRecord(
    string RelationId,
    string SourceChapterId,
    string TargetChapterId,
    string RelationType,
    string? Description,
    double Weight,
    long CreatedAt);
