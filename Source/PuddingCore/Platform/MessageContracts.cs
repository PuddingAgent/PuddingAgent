using PuddingCode.Models;
using PuddingCode.Runtime;

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
    /// <summary>
    /// KeyVault 密钥引用（推荐）。
    /// Runtime 在实际调用 LLM 前通过 IKeyVaultService 解析为明文，仅在内存中使用。
    /// </summary>
    public string? KeyVaultId { get; init; }
    /// <summary>
    /// 明文 API Key（仅为向后兼容保留，避免跨服务明文传输）。
    /// </summary>
    [Obsolete("请改用 KeyVaultId 在 Runtime 侧注入密钥；此字段仅为向后兼容保留。")]
    public string? ApiKey { get; init; }
    public string? ModelId { get; init; }
    /// <summary>
    /// 当前模型的上下文窗口，来自 LLM 服务商模型配置。
    /// Agent 模板/实例只应持有 provider/model 绑定，不能复制或兜底模型容量。
    /// </summary>
    public int? MaxContextTokens { get; init; }
    /// <summary>当前模型的最大输出长度，来自 LLM 服务商模型配置。</summary>
    public int? MaxOutputTokens { get; init; }
    /// <summary>推理深度："low" | "medium" | "high"</summary>
    public string? ReasoningEffort { get; init; }
}

/// <summary>消息入口请求。</summary>
public sealed record MessageIngressRequest
{
    public required string ChannelId { get; init; }
    public required string UserExternalId { get; init; }
    /// <summary>Canonical runtime authorization user id. Defaults to UserExternalId when omitted.</summary>
    public string? UserId { get; init; }
    /// <summary>Stable platform agent id used as runtime authorization scope when available.</summary>
    public string? AgentInstanceId { get; init; }
    public required string MessageText { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    /// <summary>Initial title for a session created by this request.</summary>
    public string? SessionTitle { get; init; }
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
    /// <summary>强制新建会话（跳过 FindActive 复用逻辑），用于"新对话"场景。</summary>
    public bool ForceNewSession { get; init; }
    /// <summary>来源元数据——由连接器（WebSocket/Webhook/MQTT 等）在 PuddingIngressEnvelope 中设置，透传至 SessionRouter metadata 帧供前端渲染来源图标/标签。</summary>
    public Dictionary<string, string>? Metadata { get; init; }
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

/// <summary>消息来源元数据，由 Dispatcher 在路由阶段填充，供 Runtime 构造 LLM 可读的消息信封。</summary>
public sealed record MessageOrigin
{
    /// <summary>发送者类型（user / agent / system）。</summary>
    public required string FromKind { get; init; }
    /// <summary>发送者标识。</summary>
    public required string FromId { get; init; }
    /// <summary>发送者展示名称。</summary>
    public string? FromDisplayName { get; init; }
    /// <summary>关联 ID，用于分布式追踪。</summary>
    public string? CorrelationId { get; init; }
    /// <summary>因果 ID，标识触发本条消息的上游事件。</summary>
    public string? CausationId { get; init; }
    /// <summary>消息类型（如 agent_message / agent_reply / subagent_result）。</summary>
    public required string MessageType { get; init; }
}

/// <summary>Runtime 执行请求——Controller 投递到 Runtime。</summary>
public sealed record RuntimeDispatchRequest
{
    public required string SessionId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string MessageText { get; init; }
    public required string WorkspaceId { get; init; }
    /// <summary>User identity used for runtime authorization and observability.</summary>
    public string? UserId { get; init; }
    /// <summary>Controller 为本次用户消息分配的消息 ID，用于将流式事件稳定绑定到前端 turn。</summary>
    public string? MessageId { get; init; }
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
    /// <summary>消息来源元数据，由 Dispatcher 填充。Runtime 在构造 LLM 上下文时据此渲染 pudding-message JSON 信封。</summary>
    public MessageOrigin? Origin { get; init; }
    /// <summary>Task planning run identity when this runtime execution is delegated from a plan.</summary>
    public string? TaskPlanId { get; init; }
    /// <summary>Current task node identity within the planning tree.</summary>
    public string? TaskNodeId { get; init; }
    /// <summary>Parent task node identity, if this execution handles a child node.</summary>
    public string? ParentTaskNodeId { get; init; }
    /// <summary>Current delegation depth where the root task node is depth 0.</summary>
    public int? DelegationDepth { get; init; }
    /// <summary>Maximum delegation depth allowed for the current plan.</summary>
    public int? MaxDelegationDepth { get; init; }
    /// <summary>Role assigned to this runtime execution within the plan.</summary>
    public string? RoleInPlan { get; init; }
    /// <summary>Whether this execution may split work into child task nodes.</summary>
    public bool? AllowSubDelegation { get; init; }
    /// <summary>Whether this execution may create task-scoped agents.</summary>
    public bool? AllowAgentCreation { get; init; }
    /// <summary>Task objective assigned to this execution, if different from MessageText.</summary>
    public string? AssignedObjective { get; init; }
    /// <summary>Expected output contract for the assigned task node.</summary>
    public string? ExpectedOutputContract { get; init; }
    /// <summary>Agent Loop 最大轮数。与 AgentExecutionGuardrails.MaxRounds(默认200) 保持一致。0 或负数表示使用护栏默认值。</summary>
    public int MaxRounds { get; init; }
    /// <summary>ADR-042: 入站消息的发送 Agent ID（agent-to-agent 消息时非空）。</summary>
    public string? InboundSourceAgentId { get; init; }
    /// <summary>ADR-042: 入站消息的发送 Agent 名称。</summary>
    public string? InboundSourceAgentName { get; init; }
    /// <summary>
    /// 禁止本次执行结束时触发上下文自动压缩。
    /// 压缩摘要请求会读取当前 Session 历史，如果再次触发压缩会形成递归。
    /// </summary>
    public bool SuppressContextAutoCompaction { get; init; }
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
    /// <summary>最后一次 LLM 调用的 prefix 缓存诊断快照。</summary>
    public PromptPrefixSnapshot? PrefixSnapshot { get; init; }
    /// <summary>执行结束时的最终状态。</summary>
    public AgentExecutionState ExecutionState { get; init; } = AgentExecutionState.Running;
    /// <summary>Loop 停止原因（对应 AgentLoopStopReason 的字符串值）。</summary>
    public string? StopReason { get; init; }
    /// <summary>进入 WaitingEvent/WaitingApproval 时生成的恢复锚点 ID，供 Controller 唤醒时回传。</summary>
    public string? ResumeAnchorId { get; init; }
    /// <summary>本次执行产生的逐轮步骤摘要（包含工具调用记录），供前端展示 Agent 思维链。</summary>
    public IReadOnlyList<TurnStepDto>? TurnSteps { get; init; }
    /// <summary>
    /// 本次执行期间失败的工具调用数。
    ///
    /// 该字段用于跨层传递“Agent 自然语言回复”和“执行基础设施事实”之间的差异：
    /// 子代理可能最终生成了一段文本，但如果文本本身是在解释工具失败，父 Agent
    /// 必须看到这个事实，不能只依据 ReplyText 非空把运行误判为成功。
    /// </summary>
    public int ToolFailureCount { get; init; }
    /// <summary>
    /// 本次执行期间检测到被工具层截断的输出数量。
    ///
    /// 截断通常发生在 shell/file/http 等工具自己的安全边界内，运行时只负责把该事实
    /// 结构化传递给子代理归档和父 Agent 通知，避免让 LLM 用自然语言猜测是否截断。
    /// </summary>
    public int ToolOutputTruncatedCount { get; init; }
    /// <summary>所有工具输出和错误文本的原始字符数合计，用于诊断大输出链路。</summary>
    public long ToolOutputChars { get; init; }
    /// <summary>首个工具失败摘要，供子代理失败消息和诊断面板展示。</summary>
    public string? ToolFailureSummary { get; init; }
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
