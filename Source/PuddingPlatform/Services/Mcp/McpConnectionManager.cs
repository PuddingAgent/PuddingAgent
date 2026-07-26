using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PuddingCode.Abstractions;
using PuddingCode.Tools;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Mcp;

public interface IMcpConnectionManager
{
    Task RefreshAllAsync(CancellationToken ct = default);
    Task RefreshWorkspaceAsync(string workspaceId, CancellationToken ct = default);
    IReadOnlyList<McpServerRuntimeStatus> ListStatuses(string workspaceId);
}

public sealed record McpServerRuntimeStatus(
    string SkillId,
    string SkillName,
    string Status,
    int ToolCount,
    string? Error,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Owns MCP client sessions and exposes an in-memory, workspace-scoped tool snapshot to the
/// synchronous Pudding registry. Database and network work only happens during reconciliation.
/// </summary>
public sealed class McpConnectionManager :
    IMcpConnectionManager,
    IWorkspacePuddingToolSource,
    IAsyncDisposable
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly IKeyVaultService _keyVault;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, McpServerSession>> _sessions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<McpServerRuntimeStatus>> _statuses =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceGates =
        new(StringComparer.Ordinal);

    public McpConnectionManager(
        IDbContextFactory<PlatformDbContext> dbFactory,
        IKeyVaultService keyVault,
        ILoggerFactory loggerFactory,
        ILogger<McpConnectionManager> logger)
    {
        _dbFactory = dbFactory;
        _keyVault = keyVault;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public string SourceId => "mcp";

    public IReadOnlyList<IPuddingTool> ListTools(string workspaceId)
    {
        if (!_sessions.TryGetValue(workspaceId, out var workspaceSessions))
            return [];

        return workspaceSessions.Values
            .SelectMany(session => session.Tools)
            .OrderBy(tool => tool.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<McpServerRuntimeStatus> ListStatuses(string workspaceId) =>
        _statuses.TryGetValue(workspaceId, out var statuses) ? statuses : [];

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var configuredWorkspaceIds = await db.WorkspaceSkills
            .AsNoTracking()
            .Where(skill => skill.SkillType == "MCP" && skill.IsEnabled)
            .Select(skill => skill.Workspace.WorkspaceId)
            .Distinct()
            .ToListAsync(ct);

        var workspaceIds = configuredWorkspaceIds
            .Concat(_sessions.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _logger.LogInformation(
            "[MCP] Reconciliation scan workspaces={WorkspaceCount}",
            workspaceIds.Count);
        foreach (var workspaceId in workspaceIds)
            await RefreshWorkspaceAsync(workspaceId, ct);
    }

    public async Task RefreshWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var gate = _workspaceGates.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var registrations = await LoadRegistrationsAsync(workspaceId, ct);
            var next = new Dictionary<string, McpServerSession>(StringComparer.OrdinalIgnoreCase);
            var statuses = new List<McpServerRuntimeStatus>(registrations.Count);

            foreach (var registration in registrations)
            {
                if (!McpServerConfig.TryParse(registration.ConfigJson, out var config, out var configError))
                {
                    statuses.Add(FailedStatus(registration, configError!));
                    _logger.LogWarning(
                        "[MCP] Invalid config workspace={WorkspaceId} skill={SkillId}: {Error}",
                        workspaceId, registration.SkillId, configError);
                    continue;
                }

                try
                {
                    var session = await ConnectAsync(registration, config!, ct);
                    next.Add(registration.SkillId, session);
                    statuses.Add(new McpServerRuntimeStatus(
                        registration.SkillId,
                        registration.Name,
                        "Available",
                        session.Tools.Count,
                        null,
                        DateTimeOffset.UtcNow));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    statuses.Add(FailedStatus(registration, ex.Message));
                    _logger.LogWarning(
                        ex,
                        "[MCP] Connection failed workspace={WorkspaceId} skill={SkillId} target={Target}",
                        workspaceId,
                        registration.SkillId,
                        SafeTarget(config!));
                }
            }

            _sessions.TryGetValue(workspaceId, out var previous);
            _sessions[workspaceId] = next;
            _statuses[workspaceId] = statuses;

            if (previous is not null)
            {
                foreach (var session in previous.Values)
                    await session.DisposeAsync();
            }

            _logger.LogInformation(
                "[MCP] Reconciled workspace={WorkspaceId} servers={ServerCount} tools={ToolCount} failures={FailureCount}",
                workspaceId,
                next.Count,
                next.Values.Sum(session => session.Tools.Count),
                statuses.Count(status => status.Status != "Available"));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<McpRegistration>> LoadRegistrationsAsync(
        string workspaceId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId, ct);
        if (workspace is null)
            return [];

        return await db.WorkspaceSkills.AsNoTracking()
            .Where(skill => skill.WorkspaceEntityId == workspace.Id
                            && skill.SkillType == "MCP"
                            && skill.IsEnabled)
            .OrderBy(skill => skill.SkillId)
            .Select(skill => new McpRegistration(
                workspaceId,
                skill.SkillId,
                skill.Name,
                skill.ConfigJson))
            .ToListAsync(ct);
    }

    private async Task<McpServerSession> ConnectAsync(
        McpRegistration registration,
        McpServerConfig config,
        CancellationToken ct)
    {
        HttpClient? httpClient = null;
        IClientTransport? transport = null;
        McpClient? client = null;
        try
        {
            if (config.Transport == "stdio")
            {
                if (config.WorkingDirectory is not null && !Directory.Exists(config.WorkingDirectory))
                {
                    throw new DirectoryNotFoundException(
                        $"MCP stdio working directory does not exist: {config.WorkingDirectory}");
                }

                var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
                transport = new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Command = config.Command,
                        Arguments = config.Arguments.ToList(),
                        Name = registration.Name,
                        WorkingDirectory = config.WorkingDirectory,
                        // Forward only the SDK's OS/runtime allowlist. Arbitrary credentials from
                        // the Pudding host must not leak into a local third-party MCP process.
                        InheritEnvironmentVariables = false,
                        EnvironmentVariables = environment,
                        ShutdownTimeout = TimeSpan.FromSeconds(config.ShutdownTimeoutSeconds),
                        StandardErrorLines = line => _logger.LogDebug(
                            "[MCP] stdio stderr workspace={WorkspaceId} skill={SkillId}: {Line}",
                            registration.WorkspaceId,
                            registration.SkillId,
                            TruncateLogLine(line)),
                    },
                    _loggerFactory);
            }
            else
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(config.BearerTokenSecretId))
                {
                    var secret = await _keyVault.GetSecretAsync(
                        config.BearerTokenSecretId,
                        includePlainText: true,
                        ct);
                    if (string.IsNullOrWhiteSpace(secret?.Value))
                    {
                        throw new InvalidOperationException(
                            "bearerTokenSecretId does not reference a readable KeyVault secret.");
                    }

                    headers["Authorization"] = $"Bearer {secret.Value}";
                }

                var handler = McpNetworkPolicy.CreateHandler(config.AllowPrivateNetwork);
                handler.ConnectTimeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds);
                httpClient = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                    MaxResponseContentBufferSize = 4 * 1024 * 1024,
                };

                transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = config.EndpointUri,
                        Name = registration.Name,
                        TransportMode = config.TransportMode,
                        ConnectionTimeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds),
                        AdditionalHeaders = headers,
                        MaxReconnectionAttempts = config.MaxReconnectionAttempts,
                        OwnsSession = true,
                    },
                    httpClient,
                    _loggerFactory,
                    ownsHttpClient: false);
            }

            client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "PuddingAgent",
                        Title = "Pudding Agent",
                        Version = "0.7.0",
                    },
                    InitializationTimeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds),
                },
                _loggerFactory,
                ct);

            var session = new McpServerSession(
                registration,
                config,
                client,
                httpClient,
                _loggerFactory.CreateLogger<McpServerSession>());
            await session.InitializeAsync(ct);
            return session;
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync();
            else if (transport is IAsyncDisposable asyncDisposableTransport)
                await asyncDisposableTransport.DisposeAsync();
            httpClient?.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var allSessions = _sessions.Values.SelectMany(value => value.Values).ToList();
        _sessions.Clear();
        foreach (var session in allSessions)
            await session.DisposeAsync();
        foreach (var gate in _workspaceGates.Values)
            gate.Dispose();
        _workspaceGates.Clear();
    }

    private static McpServerRuntimeStatus FailedStatus(McpRegistration registration, string error) =>
        new(registration.SkillId, registration.Name, "Unavailable", 0, error, DateTimeOffset.UtcNow);

    private static string SafeTarget(McpServerConfig config) => config.Transport == "stdio"
        ? Path.GetFileName(config.Command)
        : config.EndpointUri.GetLeftPart(UriPartial.Path);

    private static string TruncateLogLine(string line) => line.Length <= 2_000 ? line : line[..2_000];

    private sealed record McpRegistration(
        string WorkspaceId,
        string SkillId,
        string Name,
        string? ConfigJson);

    private sealed class McpServerSession : IAsyncDisposable
    {
        private readonly McpRegistration _registration;
        private readonly McpServerConfig _config;
        private readonly McpClient _client;
        private readonly HttpClient? _httpClient;
        private readonly ILogger _logger;
        private IPuddingTool[] _tools = [];
        private IAsyncDisposable? _toolListSubscription;

        public McpServerSession(
            McpRegistration registration,
            McpServerConfig config,
            McpClient client,
            HttpClient? httpClient,
            ILogger logger)
        {
            _registration = registration;
            _config = config;
            _client = client;
            _httpClient = httpClient;
            _logger = logger;
        }

        public IReadOnlyList<IPuddingTool> Tools => Volatile.Read(ref _tools);

        public async Task InitializeAsync(CancellationToken ct)
        {
            await RefreshToolsAsync(ct);
            _toolListSubscription = _client.RegisterNotificationHandler(
                NotificationMethods.ToolListChangedNotification,
                async (_, notificationCt) =>
                {
                    try
                    {
                        await RefreshToolsAsync(notificationCt);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(
                            ex,
                            "[MCP] tools/list_changed refresh failed workspace={WorkspaceId} skill={SkillId}",
                            _registration.WorkspaceId,
                            _registration.SkillId);
                    }
                });
        }

        private async Task RefreshToolsAsync(CancellationToken ct)
        {
            var discovered = await _client.ListToolsAsync(cancellationToken: ct);
            var next = discovered
                .Select(tool => (IPuddingTool)new McpPuddingTool(
                    _registration.WorkspaceId,
                    _registration.SkillId,
                    _registration.Name,
                    tool,
                    _config))
                .ToArray();

            var duplicate = next.GroupBy(tool => tool.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                throw new InvalidOperationException($"MCP server produced duplicate normalized tool id '{duplicate.Key}'.");

            Volatile.Write(ref _tools, next);
            _logger.LogInformation(
                "[MCP] Tools refreshed workspace={WorkspaceId} skill={SkillId} tools={ToolCount}",
                _registration.WorkspaceId,
                _registration.SkillId,
                next.Length);
        }

        public async ValueTask DisposeAsync()
        {
            if (_toolListSubscription is not null)
                await _toolListSubscription.DisposeAsync();
            await _client.DisposeAsync();
            _httpClient?.Dispose();
        }
    }
}

public sealed class McpWorkspaceSkillHostedService(
    IMcpConnectionManager manager,
    ILogger<McpWorkspaceSkillHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await manager.RefreshAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[MCP] Initial workspace MCP reconciliation failed.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
