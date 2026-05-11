using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 统一记忆召回服务——融合 Library FTS5 + MemoryFacts + MemoryPreferences 多路检索。
/// 作为潜意识子代理的"回忆"能力，在每次用户消息到达时触发。
/// 对主代理（AgentExecutionService）透明——通过 ContextPipeline RECALLED 层注入。
/// </summary>
public interface IMemoryRecallService
{
    /// <summary>
    /// 融合多路召回：Library FTS5 + MemoryFacts 模糊匹配 + MemoryPreferences 精确匹配。
    /// 返回 RRF 排序后的 top-K 记忆片段，直接注入 ContextPipeline。
    /// </summary>
    /// <param name="query">用户当前消息文本</param>
    /// <param name="workspaceId">工作区隔离</param>
    /// <param name="recentContext">近期对话轮次（用于评估上下文充足度）</param>
    /// <param name="topK">返回条数</param>
    Task<MemoryRecallResult> RecallAsync(
        string query,
        string workspaceId,
        IReadOnlyList<string>? recentContext = null,
        int topK = 10,
        CancellationToken ct = default);

    /// <summary>
    /// 可观测性：获取当前状态摘要（供 Admin UI 潜意识面板使用）。
    /// </summary>
    Task<MemoryRecallStatus> GetStatusAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>召回结果。</summary>
public sealed record MemoryRecallResult
{
    /// <summary>排序后的记忆片段。</summary>
    public IReadOnlyList<RecalledMemory> Items { get; init; } = Array.Empty<RecalledMemory>();

    /// <summary>上下文评估：当前对话是否有足够的记忆上下文。</summary>
    public bool IsContextSufficient { get; init; }

    /// <summary>评估说明。</summary>
    public string? ContextAssessment { get; init; }

    /// <summary>各路召回的命中数。</summary>
    public RecallHitStats HitStats { get; init; } = new();

    /// <summary>召回耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }
}

/// <summary>单条记忆片段。</summary>
public sealed record RecalledMemory
{
    public required string Snippet { get; init; }
    public double RelevanceScore { get; init; }
    public string Source { get; init; } = "unknown"; // "library" | "fact" | "preference"
    public string? SourceId { get; init; }
}

/// <summary>各路召回命中统计。</summary>
public sealed record RecallHitStats
{
    public int LibraryHits { get; init; }
    public int FactsHits { get; init; }
    public int PreferencesHits { get; init; }
}

/// <summary>召回状态摘要。</summary>
public sealed record MemoryRecallStatus
{
    public int TotalFacts { get; init; }
    public int TotalPreferences { get; init; }
    public int TotalBooks { get; init; }
    public long? LastRecallAt { get; init; }
    public int LastRecallItemCount { get; init; }
}
