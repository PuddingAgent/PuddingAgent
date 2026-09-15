using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tasks;

namespace PuddingCode.Runtime;

/// <summary>
/// ADR-057-D: Turn Executor 接口。
/// Agent Runtime 不输出 SSE Frame，不感知 HTTP/SSE/浏览器连接。
/// 只产生类型化领域事件流。
/// </summary>
public interface ITurnExecutor
{
    /// <summary>
    /// 执行 Turn，产生领域事件流。
    /// </summary>
    IAsyncEnumerable<TurnExecutionEvent> ExecuteAsync(
        TurnExecutionContext context,
        CancellationToken ct);
}

/// <summary>
/// Turn 执行上下文。Worker 提供，Runtime 只读。
/// ADR-057: 使用类型化字段，不再用 JSON 字符串中转。
/// </summary>
public sealed record TurnExecutionContext(
    string ConversationId,
    string WorkspaceId,
    string TurnId,
    string CommandId,
    string RunId,
    string? AgentInstanceId,
    string? AgentTemplateId,
    string MessageText,
    string? UserId,
    CapabilityPolicy? CapabilityPolicy,
    IReadOnlyList<LlmToolDefinition>? ToolDefinitions,
    IReadOnlyList<SkillPackageInfo>? SkillPackages,
    LlmInvocationProfile LlmProfile,
    LlmConfig? LlmConfig,
    int? MaxRounds,
    int? MaxElapsedSeconds,
    int? MaxToolCallsTotal,
    string? ChannelId,
    string? UserExternalId,
    RunCancellation RunCancellation,
    IReadOnlyList<string>? VisualArtifactIds,
    IReadOnlyList<string>? AudioArtifactIds,
    IReadOnlyList<LlmContentPart>? ContentParts = null,
    LlmRouteSnapshot? CallerLlmSnapshot = null
)
{
    /// <summary>P0-4f-2: 稳定 trace_id — 从 command 显式透传至 journal（可空）。</summary>
    public required string? TraceId { get; init; }

    /// <summary>由 Execution Kernel 创建的稳定身份。</summary>
    public RuntimeExecutionIdentity? ExecutionIdentity { get; init; }

    /// <summary>触发本 Turn 的稳定用户消息 ID。</summary>
    public string? InboundMessageId { get; init; }

    /// <summary>外部渠道来源；Runtime 将其渲染到 pudding-message metadata。</summary>
    public MessageOrigin? Origin { get; init; }

    /// <summary>
    /// Execution Kernel 在 Run 启动时冻结的绝对截止时间。
    /// Runtime、工具与子代理只能缩短该预算，禁止重新从当前时间放宽。
    /// </summary>
    public DateTimeOffset? ExecutionDeadlineUtc { get; init; }

    /// <summary>Canonical scheduler plan identity used by Runtime task context and journals.</summary>
    public string? TaskPlanId { get; init; }

    /// <summary>Canonical WorkUnit identity whose budget was applied to this Turn.</summary>
    public string? TaskNodeId { get; init; }

    /// <summary>Parent execution-plan node for hierarchical task context.</summary>
    public string? ParentTaskNodeId { get; init; }

    /// <summary>ADR-072 §9.1/§9.2：派发链注入的 Active Task 上下文（canonical 命令路径同样必须携带）。</summary>
    public ActiveTaskRuntimeContext? ActiveTask { get; init; }

    /// <summary>
    /// Execution Kernel 在 Run 启动时冻结的 WorkUnit Token/成本预算与模型价格。
    /// 输入容量保持冻结，累计输出/成本预算只能递减；禁止重新解析配置或放宽上限。
    /// </summary>
    public ExecutionUsageBudget? UsageBudget { get; init; }

    /// <summary>P0-4f-2: 输出所有权契约。默认 LegacySessionStream（不切流）；第 3 步 Coordinator 显式置 CoordinatorCanonical。</summary>
    public TurnOutputOwnership OutputOwnership { get; init; } = TurnOutputOwnership.LegacySessionStream;
}

/// <summary>
/// 单个 WorkUnit 的调用边界 Token/成本预算。价格单位均为每一百万 Token；
/// 当启用 MaxCost 且 PricingKnown=false 时 Runtime 必须失败关闭。
/// </summary>
public sealed record ExecutionUsageBudget
{
    /// <summary>单次模型请求输入容量；0 表示不额外限制。累计输入另行记账，不扣减此容量。</summary>
    public long MaxInputTokens { get; init; }
    /// <summary>累计输出剩余量。正值自动启用；派生归零后由 OutputLimitEnabled 保留启用状态。</summary>
    public long MaxOutputTokens { get; init; }
    public decimal MaxCost { get; init; }
    public bool OutputLimitEnabled { get; init; }
    public bool CostLimitEnabled { get; init; }
    [JsonIgnore]
    public bool HasOutputLimit => OutputLimitEnabled || MaxOutputTokens > 0;
    [JsonIgnore]
    public bool HasCostLimit => CostLimitEnabled || MaxCost > 0;
    public bool PricingKnown { get; init; }
    public decimal InputPricePer1MTokens { get; init; }
    public decimal OutputPricePer1MTokens { get; init; }
    public decimal CacheHitPricePer1MTokens { get; init; }

    /// <summary>
    /// 来源标记：预算由父执行派生。零值是否耗尽由各累计轴的启用状态判断，
    /// 不能用此标记把原本未启用的轴解释为耗尽。
    /// </summary>
    public bool IsDerivedRemainder { get; init; }

    /// <summary>
    /// 派生时父执行实际模型请求的输入峰值（0 表示未知）；仅用于诊断，
    /// 不包含子执行的累计输入，也不消耗继承的单次输入容量。
    /// </summary>
    public long PeakRoundInputTokens { get; init; }
}

/// <summary>
/// 执行取消信号。ITurnExecutor 使用此对象获取 CancellationToken。
/// </summary>
public sealed record RunCancellation(
    CancellationToken Token
);

/// <summary>
/// Runtime 领域事件。不是 SSE Frame。
/// </summary>
public sealed record TurnExecutionEvent(
    string ProducerEventId,
    string Type,
    int SchemaVersion,
    JsonElement Payload,
    bool IsTerminal,
    TurnTerminalInfo? TerminalInfo
);

/// <summary>
/// Turn 终态信息。
/// </summary>
public sealed record TurnTerminalInfo(
    TurnTerminalKind Kind,
    string? ErrorCode,
    string? ErrorMessage,
    string? Reply,
    JsonElement? Usage
)
{
    public static TurnTerminalInfo Success(string? reply, JsonElement? usage)
        => new(TurnTerminalKind.Completed, null, null, reply, usage);

    public static TurnTerminalInfo Failure(string errorCode, string errorMessage)
        => new(TurnTerminalKind.Failed, errorCode, errorMessage, null, null);

    public static TurnTerminalInfo Cancelled()
        => new(TurnTerminalKind.Cancelled, "execution_cancelled", "Turn was cancelled.", null, null);
}

/// <summary>
/// 单次执行（Turn）的输出所有权契约（P0-4f）。
/// 决定 Runtime 产出的领域事件由谁负责持久化与终态权威。
/// </summary>
public enum TurnOutputOwnership
{
    /// <summary>默认：旧路径（历史行为，逐步退役）。</summary>
    LegacySessionStream = 0,
    /// <summary>Coordinator 执行：Runtime 只产流，持久化与终态由 Journal（conversation_events）负责。</summary>
    CoordinatorCanonical = 1,
}
