using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Tasks;

namespace PuddingCode.Runtime;

/// <summary>工具调用请求。</summary>
public sealed record ToolInvocationRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? ConfigurationAgentInstanceId { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? AgentTemplateId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
    public RuntimeExecutionIdentity? ExecutionIdentity { get; init; }
    /// <summary>
    /// 当前执行在进入工具前冻结的剩余 WorkUnit Token/成本预算。
    /// 派生执行只能继承或缩小，不得重新使用原始满额预算。
    /// </summary>
    public ExecutionUsageBudget? UsageBudget { get; init; }
    /// <summary>父 Runtime Run 的绝对截止时间；工具及其派生执行只能缩短。</summary>
    public DateTimeOffset? ExecutionDeadlineUtc { get; init; }
    public int? DelegationDepth { get; init; }
    public int? MaxDelegationDepth { get; init; }
    public bool? AllowSubDelegation { get; init; }
    public string? RoleInPlan { get; init; }
    /// <summary>Active Task Runtime Context（TB-06）：随 ToolInvocationRequest 透传至 ToolExecutionContext。</summary>
    public ActiveTaskRuntimeContext? ActiveTask { get; init; }
    /// <summary>调用方模型的冻结 LLM 路由快照（ADR-077）；工具经 ToolExecutionContext.CallerLlmSnapshot 消费。</summary>
    public LlmRouteSnapshot? CallerLlmSnapshot { get; init; }
    /// <summary>Agent 显式配置的视觉辅助路由（ADR-077）；随请求透传至 ToolExecutionContext。</summary>
    public VisionHelperRouteSnapshot? CallerVisionHelperRoute { get; init; }
}

/// <summary>工具调用结果。</summary>
public sealed record ToolInvocationResult
{
    public required bool Success { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
    public string ArgsHash { get; init; } = "";
    public int OutputLength { get; init; }
    /// <summary>typed 富内容部件（ADR-077）：image_reader native 模式把图片交回调用模型的通道。</summary>
    public IReadOnlyList<PuddingCode.Models.LlmContentPart>? ToolContentParts { get; init; }
    /// <summary>同步派生执行消耗的累计 LLM 用量，供父 WorkUnit 继续扣减。</summary>
    public PuddingCode.Models.TokenUsageDto? DelegatedUsage { get; init; }
}

/// <summary>工具调用服务，统一权限、审计、耗时、错误处理。</summary>
public interface IToolInvocationService
{
    Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct = default);
}
