namespace PuddingMemoryEngine;

/// <summary>内存条目范围：Session 级或 Workspace 级。</summary>
public enum MemoryScope
{
    Session,
    Workspace,
}

/// <summary>
/// 单条记忆记录。可来自 LLM 主动写回或用户显式存储。
/// </summary>
public sealed class MemoryEntry
{
    /// <summary>唯一 ID。</summary>
    public string EntryId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>所属 SessionId（Session 级记忆必填）。</summary>
    public string? SessionId { get; init; }

    /// <summary>所属 WorkspaceId（Workspace 级记忆必填）。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>记忆标签/分类（如 "user_preference", "task_context"）。</summary>
    public string Tag { get; init; } = "general";

    /// <summary>记忆内容。</summary>
    public string Content { get; init; } = "";

    /// <summary>来源（如 agentInstanceId 或 "system"）。</summary>
    public string Source { get; init; } = "";

    /// <summary>写入时间。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>记忆所属范围。</summary>
    public MemoryScope Scope { get; init; }
}
