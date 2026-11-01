namespace PuddingMemoryGraph;

/// <summary>记忆节点的种类。种类决定半衰期与默认显示档位，而不是决定存储位置。</summary>
public enum MemoryNodeKind
{
    /// <summary>可核对的事实（路径、版本号、配置值）。</summary>
    Fact = 0,
    /// <summary>叙述性笔记（过程、结论、教训）。</summary>
    Note = 1,
    /// <summary>可执行流程（步骤、命令序列）。</summary>
    Procedure = 2,
    /// <summary>用户偏好（衰减最慢）。</summary>
    Preference = 3,
    /// <summary>产物引用（图片、报告、构建物）。</summary>
    Artifact = 4,
}

/// <summary>边的语义。`Contradicts` 与 `Supersedes` 是记忆库防「旧结论仍生效」的关键。</summary>
public enum MemoryRelation
{
    RelatedTo = 0,
    About = 1,
    Refines = 2,
    DerivedFrom = 3,
    Contradicts = 4,
    Supersedes = 5,
}

/// <summary>可见范围。跨工作区的记忆默认不可见。</summary>
public enum MemoryScope
{
    Session = 0,
    Agent = 1,
    Workspace = 2,
}

/// <summary>
/// 记忆节点。**Title / Summary / Body 三档是渐进加载的前提**：
/// 没有 L0/L1，主代理只能整篇加载，1M 上下文也撑不住。
/// </summary>
public sealed record MemoryNode
{
    public required string NodeId { get; init; }
    public required MemoryNodeKind Kind { get; init; }

    /// <summary>L0 索引文本（建议 ≤120 字符）。</summary>
    public required string Title { get; init; }

    /// <summary>L1 摘要（建议 ≤600 字符）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>L2 正文。</summary>
    public string Body { get; init; } = string.Empty;

    public MemoryScope Scope { get; init; } = MemoryScope.Workspace;

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>重要性 0..1。由人工标注与引用次数共同决定，不由时间决定。</summary>
    public double Salience { get; init; } = 0.5;

    /// <summary>新鲜度 0..1。由半衰期公式算出，**不手写**。</summary>
    public double Freshness { get; init; } = 1.0;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UnixEpoch;
    public DateTimeOffset LastAccessedAt { get; init; } = DateTimeOffset.UnixEpoch;
    public int AccessCount { get; init; }

    /// <summary>可追溯来源：会话 / 轮次 / 消息，用于「这条记忆哪来的」。</summary>
    public string? SourceSessionId { get; init; }
    public string? SourceTurnId { get; init; }
    public string? SourceMessageId { get; init; }

    /// <summary>当前版本号，配合 <see cref="MemoryNodeRevision"/> 形成 append-only 历史。</summary>
    public int Rev { get; init; } = 1;

    /// <summary>软删标记。删除不物理移除，否则「为什么这条记忆没了」永远查不出来。</summary>
    public bool Tombstoned { get; init; }
}

/// <summary>节点历史（append-only）。一次变更一行，head 存在 <see cref="MemoryNode"/> 上。</summary>
public sealed record MemoryNodeRevision
{
    public required string NodeId { get; init; }
    public required int Rev { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public required DateTimeOffset ChangedAt { get; init; }

    /// <summary>create / update / merge / archive / dream。</summary>
    public required string ChangeKind { get; init; }

    /// <summary>agent / human / dream。区分「谁改的」是回滚与信任判定的前提。</summary>
    public required string Actor { get; init; }
}

/// <summary>边。权重随共现强化、随时间衰减；关系类型决定它是否影响「该不该继续用这条记忆」。</summary>
public sealed record MemoryEdge
{
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public required MemoryRelation Relation { get; init; }

    /// <summary>0..1，越大越强。</summary>
    public double Weight { get; init; } = 1.0;

    public double Confidence { get; init; } = 0.5;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UnixEpoch;
}
