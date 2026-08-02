using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingAgent.Connectors;
using PuddingAgent.Services;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Services;

namespace PuddingHost.Hosting;

public sealed class ConnectorHostLifecycleService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ConnectorHostLifecycleService> _logger;
    private int _started;

    public ConnectorHostLifecycleService(
        IServiceProvider services,
        ILogger<ConnectorHostLifecycleService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            _logger.LogInformation("[ConnectorHost] Already started, skipping");
            return;
        }

        await RunStartupPhasesAsync(
            StartP2pAsync,
            MigrateConnectorConfigurationAsync,
            StartConnectorsAsync,
            _logger,
            cancellationToken);
    }

    internal static async Task RunStartupPhasesAsync(
        Func<CancellationToken, Task> startP2p,
        Func<CancellationToken, Task> migrateConnectorConfiguration,
        Func<CancellationToken, Task> startConnectors,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await RunNonCriticalStartupPhaseAsync(
            "P2P discovery",
            startP2p,
            logger,
            cancellationToken);
        await RunNonCriticalStartupPhaseAsync(
            "Feishu configuration migration",
            migrateConnectorConfiguration,
            logger,
            cancellationToken);
        await startConnectors(cancellationToken);
    }

    private static async Task RunNonCriticalStartupPhaseAsync(
        string phaseName,
        Func<CancellationToken, Task> phase,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await phase(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[ConnectorHost] {PhaseName} failed — continuing startup",
                phaseName);
        }
    }

    private async Task StartP2pAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[ConnectorHost] Starting P2P discovery...");
        var p2pDiscovery = _services.GetRequiredService<IP2pDiscoveryService>();
        await p2pDiscovery.StartAsync(cancellationToken);
        _logger.LogInformation("[ConnectorHost] P2P discovery started");
    }

    private async Task MigrateConnectorConfigurationAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[ConnectorHost] Migrating legacy Feishu bindings...");
        var channelConfig = _services.GetRequiredService<ChannelConfigurationFileService>();
        await channelConfig.MigrateLegacyAgentFeishuBindingsAsync(cancellationToken);
        _logger.LogInformation("[ConnectorHost] Feishu bindings migrated");
    }

    private async Task StartConnectorsAsync(CancellationToken cancellationToken)
    {
        var connectorHost = _services.GetRequiredService<ConnectorHost>();
        var connectors = _services.GetServices<IPuddingConnector>().ToList();

        var feishuFactory = _services.GetRequiredService<FeishuConnectorFactory>();
        var feishuConnectors = await feishuFactory.CreateAsync(cancellationToken);
        connectors.AddRange(feishuConnectors);

        _logger.LogInformation("[ConnectorHost] Registering {Count} connectors", connectors.Count);
        foreach (var c in connectors)
            connectorHost.Register(c);

        await connectorHost.StartAllAsync();
        _logger.LogInformation("[ConnectorHost] Started with {Count} connectors", connectors.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[ConnectorHost] Stopping connectors...");
            var connectorHost = _services.GetRequiredService<ConnectorHost>();
            await connectorHost.StopAllAsync(cancellationToken);
            _logger.LogInformation("[ConnectorHost] Connectors stopped");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[ConnectorHost] Graceful connector stop failed"); }

        try
        {
            _logger.LogInformation("[Jsonl] Flushing session writer...");
            var jsonlWriter = _services.GetRequiredService<PuddingCode.Services.JsonlSessionWriter>();
            await jsonlWriter.DisposeAsync();
            _logger.LogInformation("[Jsonl] Session writer flushed");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Jsonl] Flush on stop failed"); }

        try
        {
            _logger.LogInformation("[P2P] Stopping discovery...");
            var p2pDiscovery = _services.GetRequiredService<IP2pDiscoveryService>();
            await p2pDiscovery.StopAsync();
            _logger.LogInformation("[P2P] Discovery stopped");
        }
        catch (Exception ex) { _logger.LogError(ex, "[P2P] Discovery stop failed"); }
    }
}
