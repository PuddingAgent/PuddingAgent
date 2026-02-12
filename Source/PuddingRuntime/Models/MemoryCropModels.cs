namespace PuddingRuntime.Models;

/// <summary>
/// 裁剪前的原始数据包 — 来自 L2/L5/L6 等可变层的原始文本。
/// 进入裁剪管线前收集，裁剪后仅保留 MemorySnippet 注入 Pro。
/// </summary>
public sealed record RawContentBundle
{
    /// <summary>来源标识: L2-MEMORY-SUMMARY / L5-RECENT / L6-RECALLED / L6-AGENT-LOG</summary>
    public required string Source { get; init; }

    /// <summary>原始文本内容（可能很长）</summary>
    public required string Content { get; init; }

    /// <summary>预估 Token 数</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>元数据：召回时间、query 等</summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Flash 裁剪后的记忆片段 — 精简的高关联信息块。
/// 每条 2~3 句话，替代原始层内容注入 Pro 上下文。
/// </summary>
public sealed record MemorySnippet
{
    /// <summary>片段文本（2~3 句话）</summary>
    public required string Text { get; init; }

    /// <summary>来源层标识: L2 / L5 / L6 / L6-LOG</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>关联度分数（0.0~1.0），Flash 给出的判断</summary>
    public double Relevance { get; init; } = 1.0;

    /// <summary>是否为推测/联想内容</summary>
    public bool IsSpeculative { get; init; }
}

/// <summary>
/// 裁剪结果 — Flash 模型对原始层内容裁剪后的产物。
/// </summary>
public sealed record CropResult
{
    /// <summary>裁剪后的记忆片段列表</summary>
    public List<MemorySnippet> Snippets { get; init; } = new();

    /// <summary>裁剪前的总 Token 数</summary>
    public int InputTokens { get; init; }

    /// <summary>裁剪后的总 Token 数</summary>
    public int OutputTokens { get; init; }

    /// <summary>压缩比</summary>
    public double CompressionRatio => InputTokens > 0 ? (double)OutputTokens / InputTokens : 1.0;

    /// <summary>裁剪耗时（毫秒）</summary>
    public long ElapsedMs { get; init; }
}
