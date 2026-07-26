using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Contains("--stdio-server", StringComparer.Ordinal))
{
    await FakeCodexStdioServer.RunAsync();
    return;
}

if (args.Contains("--codex-smoke", StringComparer.Ordinal))
{
    await RunCodexSmokeAsync();
    return;
}

Console.WriteLine("=== MCP strict protocol CLI ===");

await using var server = await StrictMcpServer.StartAsync();
var passed = 0;
const int total = 5;

var transport = new HttpClientTransport(
    new HttpClientTransportOptions
    {
        Endpoint = server.Endpoint,
        Name = "PuddingMcpCli",
        TransportMode = HttpTransportMode.StreamableHttp,
        ConnectionTimeout = TimeSpan.FromSeconds(10),
        OwnsSession = true,
    },
    NullLoggerFactory.Instance);

await using (var client = await McpClient.CreateAsync(
                 transport,
                 new McpClientOptions
                 {
                     ClientInfo = new Implementation
                     {
                         Name = "PuddingMcpCli",
                         Version = "1.0.0",
                     },
                     InitializationTimeout = TimeSpan.FromSeconds(10),
                 },
                 NullLoggerFactory.Instance))
{
    var tools = await client.ListToolsAsync();
    Check("initialize + initialized lifecycle", server.SawInitialize && server.SawInitialized, ref passed);
    Check("tools/list pagination", tools.Count == 2 && tools.Any(t => t.Name == "echo") && tools.Any(t => t.Name == "add"), ref passed);

    var echo = tools.Single(t => t.Name == "echo");
    var echoResult = await echo.CallAsync(new Dictionary<string, object?> { ["message"] = "Hello MCP!" });
    var echoText = echoResult.Content.OfType<TextContentBlock>().Single().Text;
    Check("tools/call echo", echoText == "ECHO: Hello MCP!" && echoResult.IsError != true, ref passed);

    Check(
        "session + protocol headers",
        server.SawSessionHeader && server.SawProtocolVersionHeader && server.Errors.IsEmpty,
        ref passed);
}

await server.WaitForDeleteAsync(TimeSpan.FromSeconds(2));
Check("session DELETE on dispose", server.SawDelete, ref passed);

foreach (var error in server.Errors)
    Console.WriteLine($"  protocol error: {error}");
Console.WriteLine($"=== {passed}/{total} OK ===");
Environment.ExitCode = passed == total ? 0 : 1;

static async Task RunCodexSmokeAsync()
{
    const string expectedMarker = "PUDDING_CODEX_MCP_OK";
    var command = OperatingSystem.IsWindows() ? "npx.cmd" : "npx";
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
    var transport = new StdioClientTransport(
        new StdioClientTransportOptions
        {
            Command = command,
            Arguments = ["--yes", "@openai/codex@latest", "mcp-server"],
            Name = "PuddingCodexSmoke",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = line => Console.Error.WriteLine($"[codex mcp] {line}"),
        },
        NullLoggerFactory.Instance);

    await using var client = await McpClient.CreateAsync(
        transport,
        new McpClientOptions
        {
            ClientInfo = new Implementation
            {
                Name = "PuddingCodexSmoke",
                Title = "Pudding Codex MCP Smoke",
                Version = "1.0.0",
            },
            InitializationTimeout = TimeSpan.FromSeconds(60),
        },
        NullLoggerFactory.Instance,
        timeout.Token);

    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    var toolNames = tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    Console.WriteLine($"Codex MCP tools: {string.Join(", ", toolNames)}");
    if (!toolNames.Contains("codex", StringComparer.Ordinal)
        || !toolNames.Contains("codex-reply", StringComparer.Ordinal))
    {
        throw new InvalidOperationException("Codex MCP did not expose codex and codex-reply.");
    }

    var codexTool = tools.Single(tool => tool.Name == "codex");
    var result = await codexTool.CallAsync(
        new Dictionary<string, object?>
        {
            ["prompt"] = $"Reply with exactly {expectedMarker}. Do not inspect or modify files.",
            ["cwd"] = Directory.GetCurrentDirectory(),
            ["sandbox"] = "read-only",
            ["approval-policy"] = "never",
        },
        cancellationToken: timeout.Token);
    var formatted = JsonSerializer.Serialize(new
    {
        result.StructuredContent,
        content = result.Content.Select(block => JsonSerializer.SerializeToElement(block, block.GetType())),
        result.IsError,
    });
    Console.WriteLine(formatted);
    if (result.IsError == true || !formatted.Contains(expectedMarker, StringComparison.Ordinal))
        throw new InvalidOperationException("Codex MCP smoke response did not contain the expected marker.");

    Console.WriteLine("PASS real codex mcp-server tools/list + tools/call");
}

static void Check(string name, bool condition, ref int passed)
{
    Console.WriteLine($"  {(condition ? "PASS" : "FAIL")} {name}");
    if (condition) passed++;
}

internal sealed class StrictMcpServer : IAsyncDisposable
{
    private const string SessionId = "pudding-mcp-test-session";
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _deleteSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runTask;
    private string? _protocolVersion;

    public required Uri Endpoint { get; init; }
    public bool SawInitialize { get; private set; }
    public bool SawInitialized { get; private set; }
    public bool SawSessionHeader { get; private set; }
    public bool SawProtocolVersionHeader { get; private set; }
    public bool SawDelete { get; private set; }
    public ConcurrentQueue<string> Errors { get; } = new();

    public static Task<StrictMcpServer> StartAsync()
    {
        var port = ReservePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp/");
        var server = new StrictMcpServer { Endpoint = endpoint };
        server._listener.Prefixes.Add(endpoint.ToString());
        server._listener.Start();
        server._runTask = server.RunAsync();
        return Task.FromResult(server);
    }

    public async Task WaitForDeleteAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { await _deleteSeen.Task.WaitAsync(cts.Token); }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                _ = HandleAsync(context);
            }
        }
        catch (HttpListenerException) when (_cts.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_cts.IsCancellationRequested) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "GET")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod == "DELETE")
            {
                ValidateSessionHeaders(context.Request);
                SawDelete = true;
                _deleteSeen.TrySetResult();
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Close();
                return;
            }

            ValidateAcceptHeader(context.Request);
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();

            if (method == "initialize")
            {
                SawInitialize = true;
                ValidateInitialize(root);
                context.Response.Headers["Mcp-Session-Id"] = SessionId;
                await WriteJsonAsync(context.Response, new
                {
                    jsonrpc = "2.0",
                    id = root.GetProperty("id").Clone(),
                    result = new
                    {
                        protocolVersion = _protocolVersion,
                        capabilities = new { tools = new { listChanged = true } },
                        serverInfo = new { name = "StrictMcpServer", version = "1.0.0" },
                    },
                });
                return;
            }

            ValidateSessionHeaders(context.Request);
            if (method == NotificationMethods.InitializedNotification)
            {
                SawInitialized = true;
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
                return;
            }

            if (method == "tools/list")
            {
                var cursor = root.TryGetProperty("params", out var parameters)
                             && parameters.TryGetProperty("cursor", out var cursorElement)
                    ? cursorElement.GetString()
                    : null;
                object payload = cursor == "page-2"
                    ? (object)new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").Clone(),
                        result = new
                        {
                            tools = new object[]
                            {
                                new
                                {
                                    name = "add",
                                    description = "Adds two numbers",
                                    inputSchema = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            a = new { type = "integer" },
                                            b = new { type = "integer" },
                                        },
                                        required = new[] { "a", "b" },
                                    },
                                },
                            },
                        },
                    }
                    : new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").Clone(),
                        result = new
                        {
                            tools = new object[]
                            {
                                new
                                {
                                    name = "echo",
                                    description = "Echoes a message",
                                    inputSchema = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            message = new
                                            {
                                                type = "string",
                                                @enum = new[] { "Hello MCP!" },
                                            },
                                        },
                                        required = new[] { "message" },
                                    },
                                    annotations = new { readOnlyHint = true },
                                },
                            },
                            nextCursor = "page-2",
                        },
                    };
                await WriteJsonAsync(context.Response, payload);
                return;
            }

            if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var toolName = parameters.GetProperty("name").GetString();
                object result = toolName switch
                {
                    "echo" => new
                    {
                        content = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = $"ECHO: {parameters.GetProperty("arguments").GetProperty("message").GetString()}",
                            },
                        },
                        isError = false,
                    },
                    _ => new
                    {
                        content = new object[] { new { type = "text", text = "unknown tool" } },
                        isError = true,
                    },
                };
                await WriteJsonAsync(context.Response, new
                {
                    jsonrpc = "2.0",
                    id = root.GetProperty("id").Clone(),
                    result,
                });
                return;
            }

            await WriteJsonAsync(context.Response, new
            {
                jsonrpc = "2.0",
                id = root.TryGetProperty("id", out var id) ? id.Clone() : default,
                error = new { code = -32601, message = $"Method not found: {method}" },
            });
        }
        catch (Exception ex)
        {
            Errors.Enqueue(ex.Message);
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
        }
    }

    private void ValidateInitialize(JsonElement root)
    {
        var parameters = root.GetProperty("params");
        _protocolVersion = parameters.GetProperty("protocolVersion").GetString()
                           ?? throw new InvalidOperationException("protocolVersion is missing.");
        _ = parameters.GetProperty("clientInfo");
        _ = parameters.GetProperty("capabilities");
        if (parameters.TryGetProperty("protocol_version", out _)
            || parameters.TryGetProperty("client_info", out _))
        {
            throw new InvalidOperationException("initialize used snake_case fields.");
        }
    }

    private void ValidateAcceptHeader(HttpListenerRequest request)
    {
        var accept = request.Headers["Accept"] ?? string.Empty;
        if (!accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || !accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            Errors.Enqueue($"Invalid Accept header: {accept}");
        }
    }

    private void ValidateSessionHeaders(HttpListenerRequest request)
    {
        SawSessionHeader |= request.Headers["Mcp-Session-Id"] == SessionId;
        SawProtocolVersionHeader |= request.Headers["MCP-Protocol-Version"] == _protocolVersion;
        if (request.Headers["Mcp-Session-Id"] != SessionId)
            Errors.Enqueue("Mcp-Session-Id is missing or incorrect.");
        if (request.Headers["MCP-Protocol-Version"] != _protocolVersion)
            Errors.Enqueue("MCP-Protocol-Version is missing or incorrect.");
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        if (_runTask is not null)
            await _runTask;
        _cts.Dispose();
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Minimal JSONL MCP process used by PuddingPlatformTests to exercise the real stdio transport.
/// Stdout is protocol-only; diagnostics must go to stderr.
/// </summary>
internal static class FakeCodexStdioServer
{
    private const string ThreadId = "fake-codex-thread-1";

    public static async Task RunAsync()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        await using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var method = root.GetProperty("method").GetString();
                if (!root.TryGetProperty("id", out var id))
                    continue;

                var response = method switch
                {
                    "initialize" => Initialize(id, root),
                    "tools/list" => ToolList(id),
                    "tools/call" => ToolCall(id, root),
                    "ping" => new { jsonrpc = "2.0", id = id.Clone(), result = new { } },
                    _ => new
                    {
                        jsonrpc = "2.0",
                        id = id.Clone(),
                        error = new { code = -32601, message = $"Method not found: {method}" },
                    },
                };

                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"fake stdio MCP error: {ex.Message}");
            }
        }
    }

    private static object Initialize(JsonElement id, JsonElement root)
    {
        var protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString();
        return new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            result = new
            {
                protocolVersion,
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "Fake Codex MCP", version = "1.0.0" },
            },
        };
    }

    private static object ToolList(JsonElement id) => new
    {
        jsonrpc = "2.0",
        id = id.Clone(),
        result = new
        {
            tools = new object[]
            {
                new
                {
                    name = "codex",
                    description = "Starts a fake Codex task.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            prompt = new { type = "string" },
                            cwd = new { type = "string" },
                            sandbox = new { type = "string" },
                        },
                        required = new[] { "prompt" },
                        additionalProperties = false,
                    },
                },
                new
                {
                    name = "codex-reply",
                    description = "Continues a fake Codex task.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            threadId = new { type = "string" },
                            prompt = new { type = "string" },
                        },
                        required = new[] { "threadId", "prompt" },
                        additionalProperties = false,
                    },
                },
            },
        },
    };

    private static object ToolCall(JsonElement id, JsonElement root)
    {
        var parameters = root.GetProperty("params");
        var toolName = parameters.GetProperty("name").GetString();
        var arguments = parameters.GetProperty("arguments");
        var prompt = arguments.GetProperty("prompt").GetString();
        var content = toolName switch
        {
            "codex" => $"FAKE CODEX START: {prompt}",
            "codex-reply" when arguments.GetProperty("threadId").GetString() == ThreadId =>
                $"FAKE CODEX REPLY: {prompt}",
            _ => "unknown fake Codex tool or thread",
        };

        var isError = toolName is not ("codex" or "codex-reply")
                      || (toolName == "codex-reply"
                          && arguments.GetProperty("threadId").GetString() != ThreadId);
        return new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            result = new
            {
                structuredContent = new { threadId = ThreadId, content },
                content = new object[] { new { type = "text", text = content } },
                isError,
            },
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
