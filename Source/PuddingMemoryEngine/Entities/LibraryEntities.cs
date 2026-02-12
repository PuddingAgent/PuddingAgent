using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>图书馆容器——一个 Workspace 下的 Agent 可以拥有多个 Library。</summary>
public class LibraryEntity
{
    [Key]
    [MaxLength(32)]
    public string LibraryId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>
    /// 所属 AgentId。为空表示 ADR-030 agent 绑定前的 legacy workspace-only 图书馆。
    /// </summary>
    [MaxLength(64)]
    public string? AgentId { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>书籍——独立的完整记忆单元。一个主题一本书。</summary>
public class BookEntity
{
    [Key]
    [MaxLength(32)]
    public string BookId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(32)]
    public string LibraryId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Status { get; set; } = "active";

    public int Version { get; set; } = 1;

    public int AccessCount { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long? LastAccessedAt { get; set; }

    /// <summary>嵌入向量（float32 字节数组，dim=1536，Base64 存储）。</summary>
    [MaxLength(1536 * 4)]
    public byte[]? Embedding { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public ICollection<BookIndexEntity> Indexes { get; set; } = new List<BookIndexEntity>();
}

/// <summary>书籍的多路径索引——每本书可以有多个 Tag 路径。</summary>
public class BookIndexEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(32)]
    public string BookId { get; set; } = string.Empty;

    [MaxLength(500)]
    public string TagPath { get; set; } = string.Empty;

    public int Weight { get; set; } = 1;

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>章节——书籍的最小经验单元。每章记录一段完整的经验。</summary>
public class ChapterEntity
{
    [Key]
    [MaxLength(32)]
    public string ChapterId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(32)]
    public string BookId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public int ChapterOrder { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>Title 的 jieba 分词结果（空格连接，供 FTS5 索引）。</summary>
    [MaxLength(4096)]
    public string TitleTokens { get; set; } = string.Empty;

    /// <summary>Content 的 jieba 分词结果（空格连接，供 FTS5 索引）。</summary>
    [MaxLength(16384)]
    public string ContentTokens { get; set; } = string.Empty;

    [MaxLength(32)]
    public string ContentType { get; set; } = "markdown";

    public double Importance { get; set; } = 0.5;

    /// <summary>章节版本状态：active | superseded。</summary>
    [MaxLength(32)]
    public string Status { get; set; } = "active";

    /// <summary>当 Status=superseded 时，指向取代它的新章节。</summary>
    [MaxLength(32)]
    public string? SupersededByChapterId { get; set; }

    /// <summary>被取代时间（Unix 时间戳，毫秒）。</summary>
    public long? SupersededAt { get; set; }

    /// <summary>Agent 实例 ID（null = 共享知识）。ADR-042 Agent 记忆隔离。</summary>
    [MaxLength(64)]
    public string? AgentInstanceId { get; set; }

    /// <summary>来源会话（可回溯完整现场）。</summary>
    [MaxLength(64)]
    public string? SourceSessionId { get; set; }

    public int WordCount { get; set; }

    /// <summary>嵌入向量（float32 字节数组，dim=1536，Base64 存储）。</summary>
    [MaxLength(1536 * 4)]
    public byte[]? Embedding { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>来源引用（内部文件路径或外部URL，用于核实）</summary>
    [MaxLength(1024)]
    public string? SourceReference { get; set; }

    /// <summary>引用类型：internal（会话记录）/ external（外部URL）/ none</summary>
    [MaxLength(32)]
    public string? ReferenceType { get; set; }

    // ── Phase 1: 元数据扩展 ──

    /// <summary>适用场景：何时/何地/何种情况下使用此记忆。</summary>
    [MaxLength(500)]
    public string? Scene { get; set; }

    /// <summary>约束条件：使用此记忆的限制和前提。</summary>
    [MaxLength(2000)]
    public string? Constraints { get; set; }

    /// <summary>标签：逗号分隔的 Tag 列表，如 "bug-fix,scroll,frontend"。</summary>
    [MaxLength(1000)]
    public string? Tags { get; set; }
}

/// <summary>章节关联关系——Chapter 之间的多对多关联，形成知识图谱边。</summary>
public class ChapterRelationEntity
{
    [Key]
    [MaxLength(32)]
    public string RelationId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>源章节 ID。</summary>
    [MaxLength(32)]
    public string SourceChapterId { get; set; } = string.Empty;

    /// <summary>目标章节 ID。</summary>
    [MaxLength(32)]
    public string TargetChapterId { get; set; } = string.Empty;

    /// <summary>关联类型：related_to / contradicts / depends_on / extends / supersedes / same_topic</summary>
    [MaxLength(100)]
    public string RelationType { get; set; } = string.Empty;

    /// <summary>关联原因描述。</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>关联强度 0-1。</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>指针——章节之间的交叉引用。ADR-029: 泛化为通用指针。</summary>
public class PointerEntity
{
    [Key]
    [MaxLength(32)]
    public string PointerId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>ADR-029: workspace 隔离。</summary>
    [MaxLength(64)]
    public string? WorkspaceId { get; set; }

    /// <summary>ADR-029: 来源类型（chapter|book|tree_node）。</summary>
    [MaxLength(32)]
    public string? SourceType { get; set; }

    /// <summary>ADR-029: 来源 ID。</summary>
    [MaxLength(32)]
    public string? SourceId { get; set; }

    /// <summary>兼容旧 API 的 ChapterId 字段。</summary>
    [MaxLength(32)]
    public string ChapterId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string TargetType { get; set; } = string.Empty;

    [MaxLength(256)]
    public string TargetId { get; set; } = string.Empty;

    public string? TargetLabel { get; set; }

    public string? Description { get; set; }

    public int Relevance { get; set; } = 5;

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>分支——Book 的平行版本。</summary>
public class BranchEntity
{
    [Key]
    [MaxLength(32)]
    public string BranchId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(32)]
    public string BookId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string BranchName { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>创建者 AgentId / UserId。</summary>
    [MaxLength(64)]
    public string? CreatedBy { get; set; }

    /// <summary>合并目标分支 ID。</summary>
    [MaxLength(32)]
    public string? MergedInto { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>来源引用——一等溯源数据结构。ADR-028 Phase 2。</summary>
public class SourceReferenceEntity
{
    [Key]
    [MaxLength(32)]
    public string SourceReferenceId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string OwnerType { get; set; } = string.Empty;

    [MaxLength(32)]
    public string OwnerId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string TargetType { get; set; } = string.Empty;

    [MaxLength(256)]
    public string TargetId { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? TargetRange { get; set; }

    [MaxLength(200)]
    public string? Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>记忆树节点——长期目录模型。ADR-028 Phase 3。</summary>
public class MemoryTreeNodeEntity
{
    [Key]
    [MaxLength(32)]
    public string NodeId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string LibraryId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? ParentNodeId { get; set; }

    [MaxLength(500)]
    public string Path { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Summary { get; set; }

    [MaxLength(32)]
    public string NodeType { get; set; } = "category";

    [MaxLength(32)]
    public string Status { get; set; } = "active";

    public int SortOrder { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Book 挂载到 TreeNode 的关系。ADR-028 Phase 3。</summary>
public class BookTreeMountEntity
{
    [Key]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(32)]
    public string BookId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string NodeId { get; set; } = string.Empty;

    public int Weight { get; set; } = 1;

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
