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
    string? ChannelId,
    string? UserExternalId,
    RunCancellation RunCancellation
);

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
