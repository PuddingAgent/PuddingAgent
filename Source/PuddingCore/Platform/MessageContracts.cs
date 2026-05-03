using PuddingCode.Models;

namespace PuddingCode.Platform;

/// <summary>
/// Skill 包摘要——随 Dispatch 请求下发给 Runtime，Runtime 据此下载并挂载。
/// </summary>
public sealed record SkillPackageInfo
{
    /// <summary>Skill 包唯一标识（即目录名），如 "my-skill-pack"</summary>
    public required string SkillPackageId { get; init; }
    /// <summary>展示名称</summary>
    public required string Name { get; init; }
    /// <summary>简短用途描述（注入 Agent 系统提示时使用）</summary>
    public string? Description { get; init; }
    /// <summary>版本号</summary>
    public string? Version { get; init; }
    /// <summary>预签名下载 URL（有效期 24h），Runtime 使用 HttpClient 直接下载。</summary>
    public required string DownloadUrl { get; init; }
}

/// <summary>
/// LLM 配置快照——由 Platform 在入口点解析 DB 配置后随请求下发，
/// Controller/Runtime 无需直接查询 DB 或依赖静态 .env。
/// </summary>
public sealed record LlmConfig
{
    /// <summary>API 基础地址（含 /v1，如 https://api.deepseek.com/v1）</summary>
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelId { get; init; }
}

/// <summary>消息入口请求。</summary>
public sealed record MessageIngressRequest
{
    public required string ChannelId { get; init; }
    public required string UserExternalId { get; init; }
    public required string MessageText { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    /// <summary>可选：显式指定要路由到的 Agent 模板 ID（如 code-agent）。</summary>
    public string? AgentTemplateId { get; init; }
    public string? MessageType { get; init; }
    public string? CorrelationId { get; init; }
    /// <summary>由 Platform 解析 Agent 配置后注入；Controller/Runtime 优先使用此值。</summary>
    public LlmConfig? LlmConfig { get; init; }
    /// <summary>由 Platform 解析 Agent 模板能力策略后注入；Runtime 用于过滤可用 Skill。</summary>
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    /// <summary>由 Platform 注入的 function-call 工具定义（来源于能力注册表）。</summary>
    public IReadOnlyList<LlmToolDefinition>? ToolDefinitions { get; init; }
    /// <summary>Agent 模板关联的 Skill 包列表（含预签名下载 URL）。由 Platform 解析后注入，经 Controller 透传至 Runtime。</summary>
    public IReadOnlyList<SkillPackageInfo>? SkillPackages { get; init; }
}

/// <summary>消息入口响应。</summary>
public sealed record MessageIngressResponse
{
    public required string MessageId { get; init; }
    public required string SessionId { get; init; }
    public string? RouteDecisionId { get; init; }
    public string? Reply { get; init; }
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>本次 LLM 调用返回的 token 用量统计。</summary>
    public TokenUsageDto? Usage { get; init; }
    /// <summary>本次执行产生的逐轮步骤摘要（包含工具调用记录），由 Runtime → Controller → Platform 逐层透传。</summary>
    public IReadOnlyList<TurnStepDto>? TurnSteps { get; init; }
}

/// <summary>Runtime 执行请求——Controller 投递到 Runtime。</summary>
public sealed record RuntimeDispatchRequest
{
    public required string SessionId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string MessageText { get; init; }
    public required string WorkspaceId { get; init; }
    public string? AgentInstanceId { get; init; }
    public PermissionSnapshot? PermissionSnapshot { get; init; }
    /// <summary>由 Platform 解析后下发的 LLM 配置；Runtime 转发给 Controller LLM 代理。</summary>
    public LlmConfig? LlmConfig { get; init; }
    /// <summary>由 Platform 解析 Agent 模板能力策略后下发；Runtime 用于过滤可用 Skill。null 时回走 BuiltIn 模板查找。</summary>
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    /// <summary>由 Platform 注入的 function-call 工具定义（来源于能力注册表）。</summary>
    public IReadOnlyList<LlmToolDefinition>? ToolDefinitions { get; init; }
    /// <summary>Agent 模板关联的 Skill 包列表（含预签名下载 URL）。Runtime 启动容器时下载并挂载至 /skills/。</summary>
    public IReadOnlyList<SkillPackageInfo>? SkillPackages { get; init; }
}

/// <summary>Agent 执行的最终状态。</summary>
public enum AgentExecutionState
{
    Running,
    Busy,
    WaitingEvent,
    WaitingApproval,
    Completed,
    Cancelled,
    Failed,
    Frozen,
}

/// <summary>单轮工具调用步骤摘要——随 RuntimeDispatchResult 一起回传，供前端可视化展示。</summary>
public sealed record TurnStepDto
{
    public required int Round { get; init; }
    /// <summary>本轮状态（CONTINUE / DONE / WAIT / FAILED / CANCELLED）。</summary>
    public required string Status { get; init; }
    /// <summary>LLM 本轮输出的文本摘要（截断至 512 字符）。</summary>
    public string? MessageSummary { get; init; }
    /// <summary>调用的工具名称；无工具调用时为 null。</summary>
    public string? ToolName { get; init; }
    /// <summary>工具参数 JSON（原始字符串）。</summary>
    public string? ToolArgs { get; init; }
    /// <summary>工具是否执行成功；无工具调用时为 null。</summary>
    public bool? ToolSuccess { get; init; }
    /// <summary>工具执行失败时的错误摘要。</summary>
    public string? ToolError { get; init; }
    /// <summary>本轮耗时（毫秒）。</summary>
    public long? DurationMs { get; init; }
}

/// <summary>Runtime 执行结果——Runtime 回传 Controller。</summary>
public sealed record RuntimeDispatchResult
{
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? ReplyText { get; init; }
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>本次 LLM 调用返回的 token 用量统计。</summary>
    public TokenUsageDto? Usage { get; init; }
    /// <summary>执行结束时的最终状态。</summary>
    public AgentExecutionState ExecutionState { get; init; } = AgentExecutionState.Running;
    /// <summary>Loop 停止原因（对应 AgentLoopStopReason 的字符串值）。</summary>
    public string? StopReason { get; init; }
    /// <summary>进入 WaitingEvent/WaitingApproval 时生成的恢复锚点 ID，供 Controller 唤醒时回传。</summary>
    public string? ResumeAnchorId { get; init; }
    /// <summary>本次执行产生的逐轮步骤摘要（包含工具调用记录），供前端展示 Agent 思维链。</summary>
    public IReadOnlyList<TurnStepDto>? TurnSteps { get; init; }
}

/// <summary>取消执行请求——Controller 向 Runtime 下发取消信号。</summary>
public sealed record CancelExecutionRequest
{
    public required string SessionId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>冻结执行请求——Controller 向 Runtime 下发冻结信号（治理动作，优先级最高）。</summary>
public sealed record FreezeExecutionRequest
{
    public required string SessionId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>恢复执行请求——Controller 解除目标 Session 的冻结标志。</summary>
public sealed record ResumeExecutionRequest
{
    public required string SessionId { get; init; }
}

/// <summary>
/// 唤醒执行请求——外部事件命中后，Controller 投递至 Runtime
/// 以恢复处于 WaitingEvent/WaitingApproval 态的会话。
/// </summary>
public sealed record DispatchWakeupRequest
{
    public required string SessionId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string WorkspaceId { get; init; }
    /// <summary>触发唤醒的事件类型（如 "approval.granted"、"file.ready"）。</summary>
    public string? EventType { get; init; }
    /// <summary>触发唤醒的事件内容摘要，将被注入 LLM 上下文。</summary>
    public string? EventData { get; init; }
    /// <summary>对应的 ResumeAnchorId（由 Runtime 在 WAIT 时生成并随结果返回）。</summary>
    public string? ResumeAnchorId { get; init; }
    public LlmConfig? LlmConfig { get; init; }
    /// <summary>延续初次调度时的能力策略，确保唤醒后工具权限一致。</summary>
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    /// <summary>延续初次调度时的工具定义，确保唤醒后 function-call 参数 schema 一致。</summary>
    public IReadOnlyList<LlmToolDefinition>? ToolDefinitions { get; init; }
}
