using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

/// <summary>
/// 子代理权限继承模式。
/// 子代理默认继承父 Agent 的能力策略；调用方只能主动降权，不能通过工具参数越权。
/// </summary>
public static class SubAgentPermissionModes
{
        public const string Inherit = "inherit";
    public const string Low = "low";
    public const string None = "none";
}

/// <summary>单个批量子代理任务的结构化输入。</summary>
public sealed record SubAgentBatchTask
{
    public required string TaskId { get; init; }
    public required string Task { get; init; }
    public string? Question { get; init; }
    public string? Scope { get; init; }
    public string? AlreadyKnown { get; init; }
    public string? Effort { get; init; }
    public string? StopCondition { get; init; }
    public string? OutputContract { get; init; }
    public string? ExpectedOutput { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>子代理调用请求。</summary>
public sealed record SubAgentInvocationRequest
{
    public required string ParentSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? WorkingDirectory { get; init; }
    public required string ParentAgentInstanceId { get; init; }
    /// <summary>子代理父 Agent ID（映射到 SubAgentSpawnRequest.ParentAgentId）。</summary>
    public string? ParentAgentId { get; init; }
    public required string TemplateId { get; init; }
    public required string Task { get; init; }
    public string? DelegationProtocol { get; init; }
    public string? Question { get; init; }
    public string? Scope { get; init; }
    public string? AlreadyKnown { get; init; }
    public string? Effort { get; init; }
    public string? StopCondition { get; init; }
    public string? OutputContract { get; init; }
    public bool IsAsync { get; init; }
    /// <summary>调用入口解析出的不可变 LLM 配置快照。</summary>
    public required LlmConfig LlmConfig { get; init; }
    /// <summary>调用入口已解析的不可变 Provider/Profile/Model 路由身份。</summary>
        public required LlmInvocationProfile LlmProfile { get; init; }
    /// <summary>父代理上下文快照（Fork + 剪枝后）。</summary>
    public string? ParentContextSnapshot { get; init; }
    public int? MaxRounds { get; init; }
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
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
    public string PermissionMode { get; init; } = SubAgentPermissionModes.Inherit;
    public int? TimeoutSeconds { get; init; }
    /// <summary>父执行的绝对截止时间；子代理调度只能在其之前结束。</summary>
    public DateTimeOffset? ParentExecutionDeadlineUtc { get; init; }
    public string? InvocationId { get; init; }
    public string OriginToolId { get; init; } = "spawn_sub_agent";
    public RuntimeExecutionIdentity? ParentExecutionIdentity { get; init; }
}

/// <summary>子代理调用结果。</summary>
public sealed record SubAgentInvocationResult
{
    public required string SubSessionId { get; init; }
    public string? TaskId { get; init; }
    public string? RunId { get; init; }
    public required string Status { get; init; }
    public string? Reply { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 批量子代理调用请求。
/// 批量调用必须使用结构化 JSON 数组，避免 Agent 通过分隔符文本造成任务边界歧义。
/// </summary>
public sealed record SubAgentBatchInvocationRequest
{
    public required string ParentSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? WorkingDirectory { get; init; }
    public required string ParentAgentInstanceId { get; init; }
    public string? ParentAgentId { get; init; }
    public required string TemplateId { get; init; }
    public required IReadOnlyList<SubAgentBatchTask> Tasks { get; init; }
    public bool IsAsync { get; init; }
    /// <summary>调用入口解析出的不可变 LLM 配置快照。</summary>
    public required LlmConfig LlmConfig { get; init; }
    /// <summary>批次内所有任务共用的不可变 Provider/Profile/Model 路由身份。</summary>
        public required LlmInvocationProfile LlmProfile { get; init; }
    /// <summary>父代理上下文快照（Fork + 剪枝后）。</summary>
    public string? ParentContextSnapshot { get; init; }
    public int? MaxRounds { get; init; }
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
    public string? ParentTaskId { get; init; }
    public string? TaskPlanId { get; init; }
    public string? ParentTaskNodeId { get; init; }
    public int? DelegationDepth { get; init; }
    public int? MaxDelegationDepth { get; init; }
    public string? RoleInPlan { get; init; }
    public bool? AllowSubDelegation { get; init; }
    public bool? AllowAgentCreation { get; init; }
    public string PermissionMode { get; init; } = SubAgentPermissionModes.Inherit;
    public int? TimeoutSeconds { get; init; }
    /// <summary>父执行的绝对截止时间；批次内每个子代理都必须服从。</summary>
    public DateTimeOffset? ParentExecutionDeadlineUtc { get; init; }
    public string? BatchId { get; init; }
    public string OriginToolId { get; init; } = "spawn_sub_agent";
    public RuntimeExecutionIdentity? ParentExecutionIdentity { get; init; }
}

/// <summary>批量子代理调用聚合结果。</summary>
public sealed record SubAgentBatchInvocationResult
{
    public required string BatchId { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<SubAgentInvocationResult> Results { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// 子代理运行调度配置。
/// 这些值属于运行时基础设施契约，不能散落为工具或 Controller 中的硬编码常量。
/// </summary>
public sealed record SubAgentExecutionOptions
{
    public int MaxConcurrentPerTemplate { get; init; } = 3;
    public int MaxConcurrentPerWorkspace { get; init; } = 6;
    public int DefaultTimeoutSeconds { get; init; } = 3600;
    public int MaxTimeoutSeconds { get; init; } = 3600;
    /// <summary>
    /// 同步子代理必须在父 Turn 截止时间前释放的收尾窗口。
    /// 用于让父 Agent 消化结果、生成最终回复并提交唯一终态。
    /// </summary>
    public int ParentFinalizationReserveSeconds { get; init; } = 120;
    public string DefaultPermissionMode { get; init; } = SubAgentPermissionModes.Inherit;
}

/// <summary>
/// Conversation Turn 的执行预算。Hard timeout 是不可续期的最终保险丝；
/// no-progress timeout 由有效运行进度形成滑动窗口。
/// </summary>
public sealed record TurnExecutionOptions
{
    public int DefaultHardTimeoutSeconds { get; init; } = 24 * 60 * 60;
    public int MaxHardTimeoutSeconds { get; init; } = 24 * 60 * 60;
    public int NoProgressTimeoutSeconds { get; init; } = 60 * 60;
    public int WatchdogPollIntervalSeconds { get; init; } = 5;
    public int LlmFirstChunkTimeoutSeconds { get; init; } = 5 * 60;
    public int LlmStreamIdleTimeoutSeconds { get; init; } = 2 * 60;
}

public sealed record RuntimeExecutionOptions
{
    public TurnExecutionOptions Turns { get; init; } = new();
    public SubAgentExecutionOptions SubAgents { get; init; } = new();
}

/// <summary>读取并补齐运行时执行配置。</summary>
public interface IRuntimeExecutionConfigService
{
    RuntimeExecutionOptions GetOptions();
}

/// <summary>子代理调用服务，隔离父执行循环与子代理生命周期。</summary>
public interface ISubAgentInvocationService
{
    Task<SubAgentInvocationResult> InvokeAsync(SubAgentInvocationRequest request, CancellationToken ct = default);
    Task<SubAgentBatchInvocationResult> InvokeBatchAsync(SubAgentBatchInvocationRequest request, CancellationToken ct = default);
}
