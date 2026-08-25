using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

/// <summary>上下文合成请求。</summary>
public sealed record ContextAssemblyRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    /// <summary>
    /// Persistent Agent instance that owns manifest, private SKILL, goal, and memory files.
    /// Delegated executions keep an ephemeral AgentInstanceId for execution isolation while
    /// resolving durable Agent-scoped files through this stable identity.
    /// </summary>
    public string? ConfigurationAgentInstanceId { get; init; }
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
    public string? TaskPlanId { get; init; }
    public string? TaskNodeId { get; init; }
    public string? ParentTaskNodeId { get; init; }
    public int? DelegationDepth { get; init; }
    public int? MaxDelegationDepth { get; init; }
    public string? RoleInPlan { get; init; }
    public bool? AllowSubDelegation { get; init; }
    public bool? AllowAgentCreation { get; init; }
    public string? AssignedObjective { get; init; }
        public string? ExpectedOutputContract { get; init; }
    /// <summary>从父代理 Fork 并剪枝后的上下文快照。非空时 ContextPipeline 注入 INHERITED-CONTEXT 层。</summary>
    public string? ParentContextSnapshot { get; init; }

    /// <summary>P0-4f-1a step6: 本次上下文合成所属执行的 trace_id（可空；不可得时显式传 null，不 fallback 生成）。</summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Session append-only loaded tool set snapshot (Core ∪ loaded)。与逐轮重组装传相同值；
    /// 为 null 保持旧的全量 registry L1-TOOLS 索引。首组装缺失而逐轮提供时，
    /// 会在 turn1→turn2 之间产生一次必然的 system prompt 字节变化（前缀缓存 miss）。
    /// </summary>
    public IReadOnlySet<string>? LoadedToolIds { get; init; }

    /// <summary>能力策略；null 时由 ContextPipeline 自行解析（旧行为）。</summary>
    public CapabilityPolicy? Capability { get; init; }
}

/// <summary>上下文合成结果，包含消息列表、token 估算、层级摘要。</summary>
public sealed record ContextAssemblyResult
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required int EstimatedTokens { get; init; }
    public required IReadOnlyList<ContextLayerSummary> Layers { get; init; }
    /// <summary>
    /// Per-turn recalled/inbound context that must be appended with the current user
    /// message rather than inserted into the stable system-prefix cache region.
    /// </summary>
    public string? UserContextPrefix { get; init; }
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

/// <summary>
/// P0-1: context.assembled 事件的单层正文发射载荷。
/// </summary>
public sealed record ContextAssemblyLayerEmission(
    string Name,
    string ContentHash,
    string Content,
    bool Truncated);

/// <summary>
/// P0-1: context.assembled 事件发射器接口。
/// 由 PuddingPlatform 实现，将模型实际所见的 context 各层正文（脱敏后）写入 canonical Conversation Event Store。
/// 接口位于 PuddingCore（Runtime 契约），实现位于 PuddingPlatform，通过宿主 DI 绑定（参照 ISessionCompactionEventEmitter）。
/// </summary>
public interface IContextAssemblyEventEmitter
{
    Task EmitAsync(
        string sessionId,
        string workspaceId,
        string? agentId,
        string? turnId,
        string? traceId,
        IReadOnlyList<ContextAssemblyLayerEmission> layers,
        string assembledAtIso,
        CancellationToken ct = default);
}
