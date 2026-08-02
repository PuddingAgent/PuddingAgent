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
    public BrowserBridgeCommandOrigin? Origin { get; init; }
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
