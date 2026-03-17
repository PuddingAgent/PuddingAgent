using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingGateway;

/// <summary>
/// Gateway Adapter 宿主——管理所有适配器的生命周期。
/// 由 PuddingController 在启动时创建并持有。
/// </summary>
public sealed class GatewayAdapterHost
{
    private readonly ConcurrentDictionary<string, AdapterEntry> _adapters = new();
    private readonly Func<PuddingIngressEnvelope, CancellationToken, Task> _onEventReceived;
    private readonly Action<string> _log;

    public GatewayAdapterHost(
        Func<PuddingIngressEnvelope, CancellationToken, Task> onEventReceived,
        Action<string> log)
    {
        _onEventReceived = onEventReceived;
        _log = log;
    }

    /// <summary>注册适配器（尚未启动）。</summary>
    public void Register(IPuddingGatewayAdapter adapter)
    {
        var entry = new AdapterEntry(adapter, AdapterStatus.Registered);
        _adapters[adapter.Descriptor.AdapterId] = entry;
        _log($"[Gateway] Adapter registered: {adapter.Descriptor.AdapterId} ({adapter.Descriptor.AdapterType})");
    }

    /// <summary>启动指定适配器。</summary>
    public async Task StartAdapterAsync(string adapterId, CancellationToken ct = default)
    {
        if (!_adapters.TryGetValue(adapterId, out var entry))
            throw new InvalidOperationException($"Adapter not found: {adapterId}");

        entry.Status = AdapterStatus.Starting;
        var context = new GatewayAdapterContext
        {
            OnEventReceived = _onEventReceived,
            Log = _log,
            CancellationToken = ct
        };

        try
        {
            await entry.Adapter.StartAsync(context, ct);
            entry.Status = AdapterStatus.Running;
            entry.StartedAt = DateTimeOffset.UtcNow;
            _log($"[Gateway] Adapter started: {adapterId}");
        }
        catch (Exception ex)
        {
            entry.Status = AdapterStatus.Faulted;
            entry.FaultReason = ex.Message;
            _log($"[Gateway] Adapter failed to start: {adapterId} - {ex.Message}");
            throw;
        }
    }

    /// <summary>停止指定适配器。</summary>
    public async Task StopAdapterAsync(string adapterId, CancellationToken ct = default)
    {
        if (!_adapters.TryGetValue(adapterId, out var entry))
            return;

        entry.Status = AdapterStatus.Stopping;
        try
        {
            await entry.Adapter.StopAsync(ct);
            entry.Status = AdapterStatus.Stopped;
            entry.StoppedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            entry.Status = AdapterStatus.Faulted;
            entry.FaultReason = ex.Message;
        }
    }

    /// <summary>启动所有已注册适配器。</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        foreach (var id in _adapters.Keys)
            await StartAdapterAsync(id, ct);
    }

    /// <summary>停止所有适配器。</summary>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        foreach (var id in _adapters.Keys)
            await StopAdapterAsync(id, ct);
    }

    /// <summary>向指定渠道回写消息。</summary>
    public async Task PublishAsync(PuddingEgressEnvelope envelope, CancellationToken ct = default)
    {
        foreach (var entry in _adapters.Values.Where(e => e.Status == AdapterStatus.Running))
        {
            if (entry.Adapter.Descriptor.SupportedChannelTypes.Contains(envelope.ChannelId) ||
                entry.Adapter.Descriptor.AdapterId == envelope.ChannelId)
            {
                await entry.Adapter.PublishAsync(envelope, ct);
                return;
            }
        }
    }

    /// <summary>获取所有适配器运行信息。</summary>
    public IReadOnlyList<AdapterRuntimeInfo> GetAdapterInfos()
    {
        return _adapters.Values.Select(e => new AdapterRuntimeInfo
        {
            AdapterId = e.Adapter.Descriptor.AdapterId,
            Status = e.Status,
            StartedAt = e.StartedAt,
            StoppedAt = e.StoppedAt,
            FaultReason = e.FaultReason
        }).ToList();
    }

    public IPuddingGatewayAdapter? GetAdapter(string adapterId)
    {
        return _adapters.TryGetValue(adapterId, out var entry) ? entry.Adapter : null;
    }

    private sealed class AdapterEntry(IPuddingGatewayAdapter adapter, AdapterStatus status)
    {
        public IPuddingGatewayAdapter Adapter { get; } = adapter;
        public AdapterStatus Status { get; set; } = status;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? StoppedAt { get; set; }
        public string? FaultReason { get; set; }
    }
}
