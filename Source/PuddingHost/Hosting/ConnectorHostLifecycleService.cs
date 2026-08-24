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
    // Stop 最多等待后台启动排空的时间；超时后仍进入停止流程，由各连接器
    // 自己的 StopAsync 兜底清理半启动状态。
    private static readonly TimeSpan ConnectorStartDrainTimeout = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ConnectorHostLifecycleService> _logger;
    private readonly object _startTaskLock = new();
    private int _started;
    private Task? _connectorStartTask;

    public ConnectorHostLifecycleService(
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        ILogger<ConnectorHostLifecycleService> logger)
    {
        _services = services;
        _lifetime = lifetime;
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
            RegisterConnectorsAsync,
            _logger,
            cancellationToken);

        // 连接器启动含远程 I/O（Feishu WS 端点发现 + 握手）。Host 的 Ready 信号
        // 在所有 hosted service StartAsync 返回后才发出，Desktop 固定 60s 超时；
        // 外网不可达时同步等待会让 Core 永远发不出 Ready。注册是本地快速操作
        // 保持同步；启动放后台，单个连接器失败仍按现有语义隔离进 Faulted。
        // 后台任务使用 ApplicationStopping：StartAsync 传入的 token 在 Host
        // 启动结束后其源会被释放，继续注册回调会抛 ObjectDisposedException。
        lock (_startTaskLock)
        {
            _connectorStartTask = Task.Run(
                () => StartConnectorsAsync(_lifetime.ApplicationStopping));
        }
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

    private async Task RegisterConnectorsAsync(CancellationToken cancellationToken)
    {
        var connectorHost = _services.GetRequiredService<ConnectorHost>();
        var connectors = _services.GetServices<IPuddingConnector>().ToList();

        var feishuFactory = _services.GetRequiredService<FeishuConnectorFactory>();
        var feishuConnectors = await feishuFactory.CreateAsync(cancellationToken);
        connectors.AddRange(feishuConnectors);

        _logger.LogInformation("[ConnectorHost] Registering {Count} connectors", connectors.Count);
        foreach (var c in connectors)
            connectorHost.Register(c);
    }

    private async Task StartConnectorsAsync(CancellationToken ct)
    {
        try
        {
            var connectorHost = _services.GetRequiredService<ConnectorHost>();
            await connectorHost.StartAllAsync(ct);
            _logger.LogInformation(
                "[ConnectorHost] Started with {Count} connectors",
                connectorHost.GetDescriptors().Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("[ConnectorHost] Connector startup cancelled by shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ConnectorHost] Connector background startup failed");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? startTask;
        lock (_startTaskLock)
            startTask = _connectorStartTask;

        if (startTask is not null)
        {
            try
            {
                await startTask.WaitAsync(ConnectorStartDrainTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "[ConnectorHost] Connector startup still running after {Seconds}s; stopping anyway",
                    ConnectorStartDrainTimeout.TotalSeconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 停止令牌已取消：直接进入停止流程，由各连接器 StopAsync 兜底。
            }
        }

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
