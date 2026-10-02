using System.Text.Json;

namespace HarnessAgent.Core.Mcp;

/// <summary>MCP tool definition returned by tools/list.</summary>
public sealed record McpTool
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required JsonElement InputSchema { get; init; }
}

/// <summary>MCP resource definition returned by resources/list.</summary>
public sealed record McpResource
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string MimeType { get; init; } = "";
}

/// <summary>MCP server capabilities from initialize response.</summary>
public sealed record McpServerCapabilities
{
    public bool SupportsTools { get; init; }
    public bool SupportsResources { get; init; }
    public bool SupportsPrompts { get; init; }
    public string ServerName { get; init; } = "";
    public string ServerVersion { get; init; } = "";
}

/// <summary>Result of a tool call via MCP.</summary>
public sealed record McpToolCallResult
{
    public required JsonElement Content { get; init; }
    public bool IsError { get; init; }
}

/// <summary>Transport abstraction for MCP connections.</summary>
public interface IMcpTransport : IDisposable
{
    /// <summary>Send a JSON-RPC request and await the response.</summary>
    Task<JsonRpc.Response> SendRequestAsync(JsonRpc.Request request, CancellationToken ct = default);

    /// <summary>Send a JSON-RPC notification (fire-and-forget).</summary>
    Task SendNotificationAsync(JsonRpc.Notification notification, CancellationToken ct = default);
}
