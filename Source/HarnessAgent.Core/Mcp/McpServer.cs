using System.Text.Json;

namespace HarnessAgent.Core.Mcp;

/// <summary>
/// MCP Server — exposes tools, resources, and prompts via JSON-RPC.
/// Implements the server side of Model Context Protocol.
/// </summary>
public sealed class McpServer
{
    private readonly Dictionary<string, McpToolDefinition> _tools = new();
    private McpServerCapabilities _capabilities = new();

    /// <summary>Server metadata.</summary>
    public string ServerName { get; init; } = "HarnessAgent-MCP";
    public string ServerVersion { get; init; } = "0.1.0";

    /// <summary>Registered tools.</summary>
    public IReadOnlyDictionary<string, McpToolDefinition> Tools => _tools;

    // ── Tool Registration ──

    /// <summary>
    /// Register a tool that can be called via MCP.
    /// </summary>
    public McpServer RegisterTool(string name, string description,
        JsonElement inputSchema, Func<JsonElement, CancellationToken, Task<string>> handler)
    {
        _tools[name] = new McpToolDefinition
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            Handler = handler,
        };
        _capabilities = _capabilities with { SupportsTools = true };
        return this;
    }

    // ── Request Handling ──

    /// <summary>
    /// Handle an incoming JSON-RPC request and return the response JSON.
    /// </summary>
    public async Task<string> HandleRequestAsync(string json, CancellationToken ct = default)
    {
        var (req, _, _, notification) = JsonRpc.Parse(json);

        // Notification — fire and forget
        if (notification != null)
        {
            if (notification.Method == "notifications/initialized")
                return "{}"; // ack silently
            return "{}";
        }

        if (req == null)
        {
            // It's a response from the client side — server usually doesn't process these
            return "{}";
        }

        return req.Method switch
        {
            "initialize" => HandleInitialize(req),
            "tools/list" => HandleToolsList(req),
            "tools/call" => await HandleToolsCallAsync(req, ct),
            "resources/list" => HandleResourcesList(req),
            _ => ErrorResponse(req.Id, -32601, $"Method not found: {req.Method}"),
        };
    }

    // ── Method Handlers ──

    private string HandleInitialize(JsonRpc.Request req)
    {
        var caps = new
        {
            capabilities = new
            {
                tools = _capabilities.SupportsTools ? new { } : null,
                resources = _capabilities.SupportsResources ? new { } : null,
            },
            serverInfo = new
            {
                name = ServerName,
                version = ServerVersion,
            },
            protocolVersion = "2025-06-18",
        };

        return JsonSerializer.Serialize(new JsonRpc.Response
        {
            Id = req.Id,
            Result = JsonSerializer.SerializeToElement(caps),
        });
    }

    private string HandleToolsList(JsonRpc.Request req)
    {
        var tools = _tools.Values.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.InputSchema,
        }).ToArray();

        var result = JsonSerializer.SerializeToElement(new { tools });
        return JsonSerializer.Serialize(new JsonRpc.Response
        {
            Id = req.Id,
            Result = result,
        });
    }

    private async Task<string> HandleToolsCallAsync(JsonRpc.Request req, CancellationToken ct)
    {
        var p = req.Params!.Value;
        var name = p.GetProperty("name").GetString()!;

        if (!_tools.TryGetValue(name, out var tool))
        {
            return ErrorResponse(req.Id, -32602, $"Unknown tool: {name}");
        }

        try
        {
            var args = p.TryGetProperty("arguments", out var a)
                ? a : JsonSerializer.SerializeToElement(new { });
            var text = await tool.Handler(args, ct);

            var result = JsonSerializer.SerializeToElement(new
            {
                content = new[]
                {
                    new { type = "text", text },
                },
            });

            return JsonSerializer.Serialize(new JsonRpc.Response
            {
                Id = req.Id,
                Result = result,
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new JsonRpc.ErrorResponse
            {
                Id = req.Id,
                Error = new JsonRpc.ErrorDetail
                {
                    Code = -32000,
                    Message = $"Tool execution failed: {ex.Message}",
                },
            });
        }
    }

    private string HandleResourcesList(JsonRpc.Request req)
    {
        var result = JsonSerializer.SerializeToElement(new { resources = Array.Empty<object>() });
        return JsonSerializer.Serialize(new JsonRpc.Response
        {
            Id = req.Id,
            Result = result,
        });
    }

    private static string ErrorResponse(int id, int code, string message)
    {
        return JsonSerializer.Serialize(new JsonRpc.ErrorResponse
        {
            Id = id,
            Error = new JsonRpc.ErrorDetail { Code = code, Message = message },
        });
    }
}

/// <summary>A registered tool with its handler.</summary>
public sealed record McpToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement InputSchema { get; init; }
    public required Func<JsonElement, CancellationToken, Task<string>> Handler { get; init; }
}
