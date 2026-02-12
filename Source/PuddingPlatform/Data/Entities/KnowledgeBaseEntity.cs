namespace PuddingPlatform.Data.Entities;

/// <summary>工作空间内的知识库（向量存储、文档检索等）。</summary>
public class KnowledgeBaseEntity
{
    public int Id { get; set; }

    /// <summary>业务层唯一 ID（GUID）。</summary>
    public string KbId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>所属工作空间的 PK。</summary>
    public int WorkspaceEntityId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>知识库类型：VectorStore | Graph | FileIndex。</summary>
    public string KbType { get; set; } = "VectorStore";

    /// <summary>已索引文档数量。</summary>
    public int DocumentCount { get; set; } = 0;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public WorkspaceEntity Workspace { get; set; } = null!;
}
