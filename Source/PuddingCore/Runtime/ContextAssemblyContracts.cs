using PuddingCode.Models;

namespace PuddingCode.Runtime;

/// <summary>上下文合成请求。</summary>
public sealed record ContextAssemblyRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string UserMessage { get; init; }
    public required string LlmProfileId { get; init; }
    public int MaxContextTokens { get; init; }
    /// <summary>是否流式模式。</summary>
    public bool ForStreaming { get; init; }
    /// <summary>是否首条消息（影响系统提示词注入策略）。</summary>
    public bool IsFirstMessage { get; init; } = true;
    /// <summary>会话历史（不含 System 消息）。</summary>
    public IReadOnlyList<ChatMessage> SessionHistory { get; init; } = Array.Empty<ChatMessage>();
}

/// <summary>上下文合成结果，包含消息列表、token 估算、层级摘要。</summary>
public sealed record ContextAssemblyResult
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required int EstimatedTokens { get; init; }
    public required IReadOnlyList<ContextLayerSummary> Layers { get; init; }
    public string? CompactionMode { get; init; }
    public string? MemoryRecallMode { get; init; }
}

/// <summary>上下文层级摘要，用于可观测性。</summary>
public sealed record ContextLayerSummary
{
    public required string Layer { get; init; }
    public required int EstimatedTokens { get; init; }
    public required int ItemCount { get; init; }
    public string? Source { get; init; }
    public string? Summary { get; init; }
}

/// <summary>上下文合成服务，包装 ContextPipeline 对外暴露稳定契约。</summary>
public interface IContextAssemblyService
{
    Task<ContextAssemblyResult> AssembleAsync(ContextAssemblyRequest request, CancellationToken ct = default);
}
