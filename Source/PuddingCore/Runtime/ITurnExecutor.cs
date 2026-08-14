using System.Text.Json;
using PuddingCode.Platform;

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
    IReadOnlyList<string>? AudioArtifactIds
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
