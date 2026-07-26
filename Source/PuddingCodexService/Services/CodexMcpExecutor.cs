using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PuddingCodexService.Models;

namespace PuddingCodexService.Services;

public sealed class CodexMcpExecutor(
    CodexServiceOptions options,
    ILoggerFactory loggerFactory,
    ILogger<CodexMcpExecutor> logger) : ICodexExecutor, IAsyncDisposable
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private McpClient? _client;
    private IReadOnlyDictionary<string, McpClientTool> _tools = new Dictionary<string, McpClientTool>();

    public async Task<CodexExecutionResult> ExecuteAsync(CodexTaskRecord task, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var toolName = task.ParentTaskId is null ? "codex" : "codex-reply";
        if (!_tools.TryGetValue(toolName, out var tool))
            throw new InvalidOperationException($"Codex MCP server does not expose required tool '{toolName}'.");

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prompt"] = task.Prompt,
        };
        if (task.ParentTaskId is null)
        {
            arguments["cwd"] = task.WorkingDirectory;
            arguments["sandbox"] = task.Sandbox;
            arguments["approval-policy"] = task.ApprovalPolicy;
            if (!string.IsNullOrWhiteSpace(task.Model))
                arguments["model"] = task.Model;
        }
        else
        {
            arguments["threadId"] = task.ThreadId
                                    ?? throw new InvalidOperationException("A reply task requires a Codex threadId.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.CallTimeoutSeconds));
        logger.LogInformation(
            "[CodexService] Executing task={TaskId} tool={ToolName} cwd={WorkingDirectory}",
            task.TaskId,
            toolName,
            task.WorkingDirectory);
        var result = await tool.CallAsync(arguments, cancellationToken: timeout.Token);
        var threadId = ExtractThreadId(result.StructuredContent) ?? task.ThreadId;
        var resultJson = JsonSerializer.Serialize(new
        {
            result.StructuredContent,
            content = result.Content.Select(block =>
                JsonSerializer.SerializeToElement(block, block.GetType(), JsonOptions)),
            isError = result.IsError == true,
        }, JsonOptions);
        return new CodexExecutionResult(threadId, resultJson, result.IsError == true);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is not null)
            return;

        await _connectGate.WaitAsync(ct);
        try
        {
            if (_client is not null)
                return;

            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = options.CodexCommand,
                    Arguments = options.CodexArguments.ToList(),
                    Name = "PuddingCodexService",
                    WorkingDirectory = options.RepositoryRoot,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = environment,
                    ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds),
                    StandardErrorLines = line => logger.LogDebug("[Codex MCP] {Line}", line),
                },
                loggerFactory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.ConnectionTimeoutSeconds));
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "PuddingCodexService",
                        Title = "Pudding Codex Service",
                        Version = "0.1.0",
                    },
                    InitializationTimeout = TimeSpan.FromSeconds(options.ConnectionTimeoutSeconds),
                },
                loggerFactory,
                timeout.Token);
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
                _tools = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
                if (!_tools.ContainsKey("codex") || !_tools.ContainsKey("codex-reply"))
                    throw new InvalidOperationException("Codex MCP server must expose codex and codex-reply.");
                _client = client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static string? ExtractThreadId(JsonElement? structuredContent)
    {
        if (structuredContent is not { ValueKind: JsonValueKind.Object } content)
            return null;
        return content.TryGetProperty("threadId", out var threadId)
               && threadId.ValueKind == JsonValueKind.String
            ? threadId.GetString()
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _connectGate.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
