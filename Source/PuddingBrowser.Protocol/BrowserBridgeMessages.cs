using System.Text.Json;

namespace PuddingBrowser.Protocol;

public sealed record BrowserBridgeHello
{
    public required int ProtocolVersion { get; init; }
    public required string DesktopInstanceId { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
}

public sealed record BrowserBridgeHelloAck
{
    public required int ProtocolVersion { get; init; }
    public required bool Accepted { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record BrowserBridgeCommandOrigin
{
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string SessionId { get; init; }
    public string? ConversationId { get; init; }
    public string? RunId { get; init; }
    public string? ToolCallId { get; init; }
    public required string ToolName { get; init; }
}

public sealed record BrowserBridgeCommand
{
    public required Guid OperationId { get; init; }
    public string? ContextId { get; init; }
    public string? PageId { get; init; }
    public required DateTimeOffset DeadlineUtc { get; init; }
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }

    /// <summary>审计用来源（诊断）：**不能**代替授权身份（缺父运行与执行 kind）。</summary>
    public BrowserBridgeCommandOrigin? Origin { get; init; }

    /// <summary>
    /// **可信调用者身份**（方案 §3.2）：由 Core 从可信执行上下文投影后随命令下发；
    /// Desktop 侧解析并校验后才执行。所有 Bridge 命令都是浏览器操作 ⇒ 缺它一律拒绝。
    /// <para>
    /// wire 形态用**字符串 kind**（`agent`/`sub_agent`/`user`），与能力通道同一套取值：
    /// 未知取值、空必填一律由 Desktop 侧拒绝（不按默认值补身份）。
    /// </para>
    /// </summary>
    public BrowserBridgeCallerIdentity? Caller { get; init; }
}

/// <summary>
/// Bridge 线缆上的调用者身份（方案 §3.2）。**不是**领域类型：`PuddingBrowser.Protocol` 不引用 Contracts，
/// 由两侧各自映射到/自 <c>Pudding.Contracts.Desktop.BrowserCallerIdentity</c>。
/// </summary>
public sealed record BrowserBridgeCallerIdentity
{
    public required string Kind { get; init; }

    public required string CallerId { get; init; }

    public string? TaskId { get; init; }

    public string? ParentTaskId { get; init; }
}

public sealed record BrowserBridgeCommandResult
{
    public required Guid OperationId { get; init; }
    public required bool Success { get; init; }
    public JsonElement? Value { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record BrowserBridgeCancel
{
    public required Guid OperationId { get; init; }
}

public sealed record BrowserBridgeEvent
{
    public required string EventType { get; init; }
    public JsonElement? Data { get; init; }
}
