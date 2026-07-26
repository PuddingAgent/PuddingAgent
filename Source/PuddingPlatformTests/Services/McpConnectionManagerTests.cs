using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Mcp;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class McpConnectionManagerTests
{
    [TestMethod]
    public async Task RefreshWorkspace_Discovers_And_Executes_Tools_Without_Crossing_Workspaces()
    {
        await using var server = await FakeMcpServer.StartAsync();
        await using var database = await TestDatabase.CreateAsync();
        var skill = await database.AddMcpSkillAsync(
            "workspace-alpha",
            "skill-alpha",
            server.Endpoint);
        await using var manager = new McpConnectionManager(
            database.Factory,
            new EmptyKeyVault(),
            NullLoggerFactory.Instance,
            NullLogger<McpConnectionManager>.Instance);

        await manager.RefreshAllAsync();

        var tool = manager.ListTools("workspace-alpha").Single();
        Assert.AreEqual("MCP", tool.Descriptor.SourceKind);
        Assert.AreEqual("skill-alpha", tool.Descriptor.SourceId);
        Assert.IsNotNull(tool.Descriptor.Parameters.RawJsonSchema);
        Assert.AreEqual(0, manager.ListTools("workspace-beta").Count);
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = """{"message":"hello"}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-alpha",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
            },
        });
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("ECHO: hello", result.Output);

        var crossWorkspace = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-2",
            ArgumentsJson = "{}",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-beta",
                SessionId = "session-2",
                AgentInstanceId = "agent-2",
            },
        });
        Assert.IsFalse(crossWorkspace.Success);
        Assert.AreEqual(403, crossWorkspace.ExitCode);

        skill.IsEnabled = false;
        await database.Db.SaveChangesAsync();
        await manager.RefreshWorkspaceAsync("workspace-alpha");
        Assert.AreEqual(0, manager.ListTools("workspace-alpha").Count);
    }

    [TestMethod]
    public async Task RefreshWorkspace_Starts_Stdio_Server_And_Preserves_Codex_Thread_Result()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fakeServerAssembly = ResolveMcpCliAssembly();
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        await database.AddMcpStdioSkillAsync(
            "workspace-codex",
            "skill-codex",
            dotnetHost,
            [fakeServerAssembly, "--stdio-server"],
            FindRepositoryRoot());
        await using var manager = new McpConnectionManager(
            database.Factory,
            new EmptyKeyVault(),
            NullLoggerFactory.Instance,
            NullLogger<McpConnectionManager>.Instance);

        await manager.RefreshWorkspaceAsync("workspace-codex");

        var tools = manager.ListTools("workspace-codex");
        Assert.HasCount(2, tools);
        var startTool = tools.Single(tool => tool.Descriptor.Name == "codex");
        var startResult = await startTool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "codex-start-1",
            ArgumentsJson = """{"prompt":"inspect this repository"}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-codex",
                SessionId = "session-codex",
                AgentInstanceId = "agent-codex",
            },
        });

        Assert.IsTrue(startResult.Success, startResult.Error);
        StringAssert.Contains(startResult.Output, "fake-codex-thread-1");
        StringAssert.Contains(startResult.Output, "FAKE CODEX START: inspect this repository");

        var replyTool = tools.Single(tool => tool.Descriptor.Name == "codex-reply");
        var replyResult = await replyTool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "codex-reply-1",
            ArgumentsJson = """{"threadId":"fake-codex-thread-1","prompt":"continue"}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-codex",
                SessionId = "session-codex",
                AgentInstanceId = "agent-codex",
            },
        });

        Assert.IsTrue(replyResult.Success, replyResult.Error);
        StringAssert.Contains(replyResult.Output, "FAKE CODEX REPLY: continue");
    }

    private static string ResolveMcpCliAssembly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var assemblyPath = Path.Combine(
            repositoryRoot,
            "Tests",
            "Mcp.Cli",
            "bin",
            configuration,
            "net10.0",
            "Mcp.Cli.dll");
        Assert.IsTrue(File.Exists(assemblyPath), $"Fake stdio MCP server was not built: {assemblyPath}");
        return assemblyPath;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the PuddingAgent repository root.");
    }

    private sealed class TestDatabase(
        SqliteConnection connection,
        PlatformDbContext db,
        IDbContextFactory<PlatformDbContext> factory) : IAsyncDisposable
    {
        public PlatformDbContext Db { get; } = db;
        public IDbContextFactory<PlatformDbContext> Factory { get; } = factory;

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new PlatformDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db, new TestDbContextFactory(options));
        }

        public async Task<WorkspaceSkillEntity> AddMcpSkillAsync(
            string workspaceId,
            string skillId,
            Uri endpoint)
        {
            var team = new TeamEntity
            {
                TeamId = "mcp-test-team",
                Name = "MCP test team",
            };
            var workspace = new WorkspaceEntity
            {
                WorkspaceId = workspaceId,
                Slug = workspaceId,
                Name = workspaceId,
                Team = team,
            };
            var skill = new WorkspaceSkillEntity
            {
                SkillId = skillId,
                Name = "Local MCP",
                SkillType = "MCP",
                IsEnabled = true,
                Workspace = workspace,
                ConfigJson = JsonSerializer.Serialize(new
                {
                    endpoint = endpoint.ToString(),
                    transport = "streamable_http",
                    allowPrivateNetwork = true,
                    connectionTimeoutSeconds = 10,
                    callTimeoutSeconds = 10,
                    maxResultChars = 4096,
                }),
            };
            Db.WorkspaceSkills.Add(skill);
            await Db.SaveChangesAsync();
            return skill;
        }

        public async Task<WorkspaceSkillEntity> AddMcpStdioSkillAsync(
            string workspaceId,
            string skillId,
            string command,
            IReadOnlyList<string> arguments,
            string workingDirectory)
        {
            var team = new TeamEntity
            {
                TeamId = "mcp-stdio-test-team",
                Name = "MCP stdio test team",
            };
            var workspace = new WorkspaceEntity
            {
                WorkspaceId = workspaceId,
                Slug = workspaceId,
                Name = workspaceId,
                Team = team,
            };
            var skill = new WorkspaceSkillEntity
            {
                SkillId = skillId,
                Name = "Fake Codex MCP",
                SkillType = "MCP",
                IsEnabled = true,
                Workspace = workspace,
                ConfigJson = JsonSerializer.Serialize(new
                {
                    transport = "stdio",
                    command,
                    arguments,
                    workingDirectory,
                    connectionTimeoutSeconds = 10,
                    callTimeoutSeconds = 30,
                    maxResultChars = 65_536,
                    shutdownTimeoutSeconds = 5,
                }),
            };
            Db.WorkspaceSkills.Add(skill);
            await Db.SaveChangesAsync();
            return skill;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class EmptyKeyVault : IKeyVaultService
    {
        public Task<string> EncryptAsync(string plainText, CancellationToken ct = default) =>
            Task.FromResult(plainText);
        public Task<string> DecryptAsync(string encryptedValue, CancellationToken ct = default) =>
            Task.FromResult(encryptedValue);
        public Task<KeyVaultSecretSummary> CreateSecretAsync(
            CreateKeyVaultSecretCommand request,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KeyVaultSecretSummary?> UpdateSecretAsync(
            string keyVaultId,
            UpdateKeyVaultSecretCommand request,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KeyVaultSecretDetail?> GetSecretAsync(
            string keyVaultId,
            bool includePlainText = false,
            CancellationToken ct = default) => Task.FromResult<KeyVaultSecretDetail?>(null);
        public Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KeyVaultSecretSummary>>([]);
        public Task<bool> DeleteSecretAsync(string keyVaultId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<string> InjectAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(text);
        public Task<string> StripAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(text);
    }

    private sealed class FakeMcpServer : IAsyncDisposable
    {
        private const string SessionId = "mcp-manager-test";
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _runTask;
        private string? _protocolVersion;

        public required Uri Endpoint { get; init; }

        public static Task<FakeMcpServer> StartAsync()
        {
            var port = ReservePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp/");
            var server = new FakeMcpServer { Endpoint = endpoint };
            server._listener.Prefixes.Add(endpoint.ToString());
            server._listener.Start();
            server._runTask = server.RunAsync();
            return Task.FromResult(server);
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
                if (context.Request.HttpMethod == "DELETE")
                {
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.Close();
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
                var root = document.RootElement;
                var method = root.GetProperty("method").GetString();
                if (method == "initialize")
                {
                    _protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString();
                    context.Response.Headers["Mcp-Session-Id"] = SessionId;
                    await WriteJsonAsync(context.Response, new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").Clone(),
                        result = new
                        {
                            protocolVersion = _protocolVersion,
                            capabilities = new { tools = new { listChanged = false } },
                            serverInfo = new { name = "MCP manager test", version = "1.0.0" },
                        },
                    });
                    return;
                }

                if (method == "notifications/initialized")
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                    context.Response.Close();
                    return;
                }

                if (method == "tools/list")
                {
                    await WriteJsonAsync(context.Response, new
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
                                            message = new { type = "string", minLength = 1 },
                                        },
                                        required = new[] { "message" },
                                        additionalProperties = false,
                                    },
                                    annotations = new { readOnlyHint = true },
                                },
                            },
                        },
                    });
                    return;
                }

                if (method == "tools/call")
                {
                    var message = root.GetProperty("params")
                        .GetProperty("arguments")
                        .GetProperty("message")
                        .GetString();
                    await WriteJsonAsync(context.Response, new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").Clone(),
                        result = new
                        {
                            content = new object[] { new { type = "text", text = $"ECHO: {message}" } },
                            isError = false,
                        },
                    });
                    return;
                }

                await WriteJsonAsync(context.Response, new
                {
                    jsonrpc = "2.0",
                    id = root.GetProperty("id").Clone(),
                    error = new { code = -32601, message = "Method not found" },
                });
            }
            catch
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.Close();
                }
            }
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
}
