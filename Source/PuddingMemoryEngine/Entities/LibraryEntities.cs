using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>图书馆容器——一个 Workspace 可以有多个 Library。</summary>
public class LibraryEntity
{
    [Key]
    [MaxLength(32)]
    public string LibraryId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

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

    [MaxLength(32)]
    public string ContentType { get; set; } = "markdown";

    public double Importance { get; set; } = 0.5;

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
}

/// <summary>指针——章节之间的交叉引用。</summary>
public class PointerEntity
{
    [Key]
    [MaxLength(32)]
    public string PointerId { get; set; } = Guid.NewGuid().ToString("N");

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
