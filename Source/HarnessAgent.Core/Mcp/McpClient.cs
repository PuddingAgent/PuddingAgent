using System.Text.Json;

namespace HarnessAgent.Core.Mcp;

/// <summary>
/// MCP (Model Context Protocol) client.
/// Connects to MCP servers via any transport (stdio, HTTP/SSE, etc.)
/// and provides tool discovery, tool calling, and resource access.
/// </summary>
public sealed class McpClient : IDisposable
{
    private readonly IMcpTransport _transport;
    private int _requestId;
    private McpServerCapabilities? _capabilities;

    public McpClient(IMcpTransport transport)
    {
        _transport = transport;
    }

    /// <summary>Server capabilities, available after InitializeAsync.</summary>
    public McpServerCapabilities? Capabilities => _capabilities;

    // ── Lifecycle ──

    /// <summary>
    /// Initialize the MCP session. Must be called first.
    /// </summary>
    public async Task<McpServerCapabilities> InitializeAsync(
        string clientName = "HarnessAgent",
        string clientVersion = "0.1.0",
        CancellationToken ct = default)
    {
        var req = new JsonRpc.Request
        {
            Id = NextId(),
            Method = "initialize",
            Params = JsonSerializer.SerializeToElement(new
            {
                protocol_version = "2025-06-18",
                client_info = new { name = clientName, version = clientVersion },
                capabilities = new { },
            }),
        };

        var resp = await _transport.SendRequestAsync(req, ct);
        _capabilities = ParseCapabilities(resp.Result);
        return _capabilities;
    }

    // ── Tools ──

    /// <summary>List available tools from the server.</summary>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var req = new JsonRpc.Request
        {
            Id = NextId(),
            Method = "tools/list",
        };

        var resp = await _transport.SendRequestAsync(req, ct);
        var tools = resp.Result.GetProperty("tools");
        var list = new List<McpTool>();

        foreach (var t in tools.EnumerateArray())
        {
            list.Add(new McpTool
            {
                Name = t.GetProperty("name").GetString()!,
                Description = t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                InputSchema = t.GetProperty("inputSchema"),
            });
        }

        return list;
    }

    /// <summary>Call a tool on the MCP server.</summary>
    public async Task<McpToolCallResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default)
    {
        var req = new JsonRpc.Request
        {
            Id = NextId(),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(new
            {
                name = toolName,
                arguments,
            }),
        };

        var resp = await _transport.SendRequestAsync(req, ct);
        var content = resp.Result.GetProperty("content");
        var isError = resp.Result.TryGetProperty("isError", out var err)
            && err.GetBoolean();

        return new McpToolCallResult
        {
            Content = content,
            IsError = isError,
        };
    }

    // ── Resources (optional) ──

    /// <summary>List available resources.</summary>
    public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        var req = new JsonRpc.Request
        {
            Id = NextId(),
            Method = "resources/list",
        };

        var resp = await _transport.SendRequestAsync(req, ct);
        var resources = resp.Result.GetProperty("resources");
        var list = new List<McpResource>();

        foreach (var r in resources.EnumerateArray())
        {
            list.Add(new McpResource
            {
                Uri = r.GetProperty("uri").GetString()!,
                Name = r.GetProperty("name").GetString()!,
                Description = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                MimeType = r.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "",
            });
        }

        return list;
    }

    // ── Internal ──

    private int NextId() => Interlocked.Increment(ref _requestId);

    private static McpServerCapabilities ParseCapabilities(JsonElement result)
    {
        var caps = result.TryGetProperty("capabilities", out var c) ? c : default;
        var serverInfo = result.TryGetProperty("serverInfo", out var si) ? si : default;

        return new McpServerCapabilities
        {
            SupportsTools = caps.ValueKind != JsonValueKind.Undefined
                && caps.TryGetProperty("tools", out _),
            SupportsResources = caps.ValueKind != JsonValueKind.Undefined
                && caps.TryGetProperty("resources", out _),
            SupportsPrompts = caps.ValueKind != JsonValueKind.Undefined
                && caps.TryGetProperty("prompts", out _),
            ServerName = serverInfo.ValueKind != JsonValueKind.Undefined
                && serverInfo.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "",
            ServerVersion = serverInfo.ValueKind != JsonValueKind.Undefined
                && serverInfo.TryGetProperty("version", out var sv) ? sv.GetString() ?? "" : "",
        };
    }

    public void Dispose() => _transport.Dispose();
}
