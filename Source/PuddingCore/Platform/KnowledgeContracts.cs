namespace PuddingCode.Platform;

// ── 知识库 ────────────────────────────────────────────────

/// <summary>知识文档条目。</summary>
public sealed record KnowledgeDocument
{
    public required string DocumentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Content { get; init; }
    public string? Title { get; init; }
    public string? SourcePath { get; init; }
    public DateTimeOffset IndexedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>知识检索结果。</summary>
public sealed record KnowledgeSearchResult
{
    public required string DocumentId { get; init; }
    public required string Content { get; init; }
    public string? Title { get; init; }
    public double Score { get; init; }
}

// ── 知识图谱 ─────────────────────────────────────────────

/// <summary>知识图谱实体。</summary>
public sealed record GraphEntity
{
    public required string EntityId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Type { get; init; }
    public required string Label { get; init; }
    public Dictionary<string, string> Properties { get; init; } = [];
}

/// <summary>知识图谱关系。</summary>
public sealed record GraphRelation
{
    public required string RelationId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string FromEntityId { get; init; }
    public required string ToEntityId { get; init; }
    public required string RelationType { get; init; }
    public double Weight { get; init; } = 1.0;
}

/// <summary>图谱查询请求。</summary>
public sealed record GraphQueryRequest
{
    /// <summary>查询关键词，匹配实体 Label 或 Type。</summary>
    public string? Keyword { get; init; }
    /// <summary>仅返回指定类型实体。</summary>
    public string? EntityType { get; init; }
    /// <summary>结果上限。</summary>
    public int Limit { get; init; } = 20;
}

// ── 统一存储 ─────────────────────────────────────────────

/// <summary>存储对象元数据。</summary>
public sealed record StorageObjectMeta
{
    public required string ObjectId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentType { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>上传请求（base64 编码内容，V1 内存实现）。</summary>
public sealed record StoragePutRequest
{
    public required string Path { get; init; }
    public required string ContentBase64 { get; init; }
    public string? ContentType { get; init; }
}

/// <summary>下载响应（base64 编码内容）。</summary>
public sealed record StorageGetResponse
{
    public required string ObjectId { get; init; }
    public required string Path { get; init; }
    public required string ContentBase64 { get; init; }
    public string? ContentType { get; init; }
}
