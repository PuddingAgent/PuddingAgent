using System.Collections.Immutable;

namespace PuddingMcp.Contracts;

// PA-05C MCP 端口（BCL-only）：连接生命周期、请求相关性、超时/未知语义、SSE 游标与 generation 围栏。
// stdio/http 传输、进程与 token 存储由适配器实现；超时/不确定结果一律 Unknown，不自动重试。

public enum McpTransportKind { Stdio, HttpSse }

public enum McpConnectionState { New, Starting, Ready, Draining, Stopped, Failed }

public sealed record McpServerRef(string ServerId, string WorkspaceId, long Generation, McpTransportKind Kind)
{
    public bool IsValid => Valid(ServerId) && Valid(WorkspaceId) && Generation >= 1;

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record McpConnectionStatus(McpConnectionState State, long Generation, string? FailureCode,
    string? Diagnostics);

public sealed record McpRequest(string RequestId, string Method, string Payload, DateTimeOffset Deadline)
{
    public bool IsValid => Valid(RequestId) && Valid(Method) && Payload.Length <= 1_048_576;

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record McpResponse(string RequestId, bool Success, string? Result, string? ErrorCode, bool Unknown);

public sealed record McpEvent(long Sequence, string Method, string Payload);

public sealed record McpEventBatch(ImmutableArray<McpEvent> Accepted, long Cursor, bool GapDetected);

/// <summary>Every call carries the host-bound server ref (workspace + generation); adapters never expose tokens.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    Task StartAsync(McpServerRef server, CancellationToken ct);
    Task<McpResponse> SendAsync(McpServerRef server, McpRequest request, CancellationToken ct);
    Task<ImmutableArray<McpEvent>> ReadEventsAsync(McpServerRef server, long afterCursor, CancellationToken ct);
}

public interface IMcpCursorStore
{
    ValueTask<long> GetAsync(string serverId, CancellationToken ct = default);
    ValueTask SetAsync(string serverId, long cursor, CancellationToken ct = default);
}

public static class McpErrorCodes
{
    public const string ServerInvalid = "mcp.server_invalid";
    public const string RequestInvalid = "mcp.request_invalid";
    public const string NotReady = "mcp.not_ready";
    public const string StaleGeneration = "mcp.stale_generation";
    public const string StartTimeout = "mcp.start_timeout";
    public const string StartFailed = "mcp.start_failed";
    public const string RequestTimeout = "mcp.request_timeout";
    public const string RequestUnknown = "mcp.request_unknown";
    public const string DrainRejected = "mcp.drain_rejected";
}

public sealed class McpException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
