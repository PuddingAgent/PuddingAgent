using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingAgent.Services;

/// <summary>
/// ConnectorHost — 管理所有 IPuddingConnector 的生命周期、事件路由和诊断。
/// 替代旧的 GatewayAdapterHost（仅兼容 IPuddingGatewayAdapter）。
/// 
/// 职责：
///   1. 注册/启动/停止连接器
///   2. OnEventReceived → 推入事件系统（通过回调注入）
///   3. 提供连接器诊断查询
///   4. 日志注入
/// </summary>
public sealed class ConnectorHost
{
    private readonly ConcurrentDictionary<string, ConnectorEntry> _connectors = new();
    private readonly Func<PuddingIngressEnvelope, CancellationToken, Task> _onEventReceived;
    private readonly ILogger<ConnectorHost> _logger;

    public ConnectorHost(
        Func<PuddingIngressEnvelope, CancellationToken, Task> onEventReceived,
        ILogger<ConnectorHost> logger)
    {
        _onEventReceived = onEventReceived;
        _logger = logger;
    }

    /// <summary>注册连接器（尚未启动）。</summary>
    public void Register(IPuddingConnector connector)
    {
        var entry = new ConnectorEntry(connector, ConnectorStatus.Registered);
        _connectors[connector.Descriptor.ConnectorId] = entry;
        _logger.LogInformation("[ConnectorHost] Registered: {Id} ({Type}) proto={Proto}",
            connector.Descriptor.ConnectorId, connector.Descriptor.ConnectorType, connector.Descriptor.Protocol);
    }

    /// <summary>启动指定连接器。</summary>
    public async Task StartAsync(string connectorId, CancellationToken ct = default)
    {
        if (!_connectors.TryGetValue(connectorId, out var entry))
            throw new InvalidOperationException($"Connector not found: {connectorId}");

        entry.Status = ConnectorStatus.Starting;
        var context = new ConnectorContext
        {
            OnEventReceived = async (envelope, c) =>
            {
                _logger.LogDebug("[ConnectorHost] Event from {ConnectorId} channel={Channel}", connectorId, envelope.ChannelType);
                await _onEventReceived(envelope, c);
            },
            Log = msg => _logger.LogInformation("[Connector:{Id}] {Msg}", connectorId, msg),
            CancellationToken = ct,
        };

        try
        {
            await entry.Connector.StartAsync(context, ct);
            entry.Status = ConnectorStatus.Running;
            entry.StartedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("[ConnectorHost] Started: {Id}", connectorId);
        }
        catch (Exception ex)
        {
            entry.Status = ConnectorStatus.Faulted;
            entry.FaultReason = ex.Message;
            _logger.LogError(ex, "[ConnectorHost] Failed to start: {Id}", connectorId);
            throw;
        }
    }

    /// <summary>停止指定连接器。</summary>
    public async Task StopAsync(string connectorId, CancellationToken ct = default)
    {
        if (!_connectors.TryGetValue(connectorId, out var entry))
            return;

        entry.Status = ConnectorStatus.Stopping;
        try
        {
            await entry.Connector.StopAsync(ct);
            entry.Status = ConnectorStatus.Stopped;
            entry.StoppedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            entry.Status = ConnectorStatus.Faulted;
            entry.FaultReason = ex.Message;
            _logger.LogError(ex, "[ConnectorHost] Failed to stop: {Id}", connectorId);
        }
    }

    /// <summary>启动所有已注册连接器。</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        foreach (var id in _connectors.Keys)
            await StartAsync(id, ct);
    }

    /// <summary>停止所有连接器。</summary>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        foreach (var id in _connectors.Keys)
            await StopAsync(id, ct);
    }

    /// <summary>获取所有连接器诊断信息。</summary>
    public async Task<IReadOnlyList<ConnectorDiagnostics>> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var results = new List<ConnectorDiagnostics>();
        foreach (var entry in _connectors.Values.Where(e => e.Status == ConnectorStatus.Running))
        {
            try
            {
                var diag = await entry.Connector.GetDiagnosticsAsync(ct);
                results.Add(diag);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ConnectorHost] GetDiagnostics failed for {Id}", entry.Connector.Descriptor.ConnectorId);
            }
        }
        return results;
    }

    /// <summary>获取所有连接器描述符。</summary>
    public IReadOnlyList<ConnectorDescriptor> GetDescriptors()
    {
        return _connectors.Values.Select(e => e.Connector.Descriptor).ToList();
    }

    private sealed class ConnectorEntry
    {
        public IPuddingConnector Connector { get; }
        public ConnectorStatus Status { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? StoppedAt { get; set; }
        public string? FaultReason { get; set; }

        public ConnectorEntry(IPuddingConnector connector, ConnectorStatus status)
        {
            Connector = connector;
            Status = status;
        }
    }
}

public enum ConnectorStatus
{
    Registered,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted,
}
