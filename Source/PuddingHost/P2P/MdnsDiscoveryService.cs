using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;

namespace PuddingAgent.P2P;

/// <summary>
/// P2P 节点发现服务。
///
/// 当前实现采用「UDP 广播 + HTTP 健康检查」作为 mDNS 不可用时的简化方案：
/// - 每 5 秒广播一次 pudding_hello；
/// - 收到广播后通过 /api/p2p/ping 主动探活；
/// - 30 秒未更新心跳视为离线并触发 PeerLost；
/// - 广播/扫描异常时使用指数退避（1s→2s→4s→...→max 30s）。
/// </summary>
public sealed class MdnsDiscoveryService : IP2pDiscoveryService
{
    private const int BroadcastPort = 9876;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OfflineThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ConcurrentDictionary<string, PeerNode> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifecycleLock = new();

    private readonly ILogger<MdnsDiscoveryService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly string _nodeId;
    private readonly string _displayName;
    private readonly string _host;
    private readonly int _port;

    private CancellationTokenSource? _lifetimeCts;
    private UdpClient? _receiver;
    private UdpClient? _sender;
    private Task? _listenLoopTask;
    private Task? _heartbeatLoopTask;

    /// <inheritdoc />
    public event EventHandler<PeerNode>? PeerDiscovered;

    /// <inheritdoc />
    public event EventHandler<PeerNode>? PeerLost;

    /// <summary>
    /// 初始化发现服务。
    /// </summary>
    public MdnsDiscoveryService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<MdnsDiscoveryService> logger)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        _nodeId = P2pNodeIdentity.ResolveNodeId(configuration);
        _displayName = P2pNodeIdentity.ResolveDisplayName(configuration);
        _port = P2pNodeIdentity.ResolvePort(configuration);
        _host = P2pNodeIdentity.ResolveHostAddress();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        lock (_lifecycleLock)
        {
            if (_lifetimeCts is not null)
            {
                _logger.LogInformation("[P2P] Discovery 已启动，忽略重复 StartAsync。nodeId={NodeId}", _nodeId);
                return Task.CompletedTask;
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            _receiver = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true,
            };
            _receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _receiver.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));

            _sender = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true,
            };

            _listenLoopTask = Task.Run(() => ListenLoopAsync(_lifetimeCts.Token), CancellationToken.None);
            _heartbeatLoopTask = Task.Run(() => HeartbeatLoopAsync(_lifetimeCts.Token), CancellationToken.None);
        }

        _logger.LogInformation(
            "[P2P] Discovery 启动成功。nodeId={NodeId} displayName={DisplayName} endpoint=http://{Host}:{Port}",
            _nodeId, _displayName, _host, _port);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? listenTask;
        Task? heartbeatTask;

        lock (_lifecycleLock)
        {
            cts = _lifetimeCts;
            listenTask = _listenLoopTask;
            heartbeatTask = _heartbeatLoopTask;

            _lifetimeCts = null;
            _listenLoopTask = null;
            _heartbeatLoopTask = null;

            _receiver?.Dispose();
            _sender?.Dispose();
            _receiver = null;
            _sender = null;
        }

        if (cts is null)
            return;

        try
        {
            cts.Cancel();
            await Task.WhenAll([listenTask ?? Task.CompletedTask, heartbeatTask ?? Task.CompletedTask]);
        }
        catch (OperationCanceledException)
        {
            // 期望内取消，无需额外处理。
        }
        finally
        {
            cts.Dispose();
            _logger.LogInformation("[P2P] Discovery 已停止。nodeId={NodeId}", _nodeId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PeerNode> GetPeers() =>
        _peers.Values
            .OrderByDescending(x => x.LastSeen)
            .ToList();

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;

            try
            {
                if (_receiver is null)
                    return;

                packet = await _receiver.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[P2P] UDP 监听异常，稍后重试。nodeId={NodeId}", _nodeId);
                continue;
            }

            try
            {
                await HandleIncomingHeartbeatAsync(packet.Buffer, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[P2P] 处理广播数据失败。nodeId={NodeId}", _nodeId);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await BroadcastHeartbeatAsync(ct);
                retryDelay = TimeSpan.FromSeconds(1);
                await Task.Delay(HeartbeatInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[P2P] 心跳广播/扫描失败，将在 {DelaySeconds}s 后重试。nodeId={NodeId}",
                    retryDelay.TotalSeconds,
                    _nodeId);

                await Task.Delay(retryDelay, ct);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
            finally
            {
                CleanupOfflinePeers();
            }
        }
    }

    /// <summary>
    /// 广播当前节点存在信息。
    /// </summary>
    private async Task BroadcastHeartbeatAsync(CancellationToken ct)
    {
        if (_sender is null)
            return;

        var payload = new HelloPacket
        {
            Type = "pudding_hello",
            NodeId = _nodeId,
            DisplayName = _displayName,
            Host = _host,
            Port = _port,
            Timestamp = DateTime.UtcNow,
        };

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        await _sender.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, BroadcastPort), ct);
    }

    /// <summary>
    /// 处理接收到的广播数据。
    /// </summary>
    private async Task HandleIncomingHeartbeatAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length == 0)
            return;

        var json = Encoding.UTF8.GetString(payload);
        var hello = JsonSerializer.Deserialize<HelloPacket>(json, JsonOptions);
        if (hello is null || !string.Equals(hello.Type, "pudding_hello", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(hello.NodeId)
            || string.IsNullOrWhiteSpace(hello.Host)
            || hello.Port <= 0
            || string.Equals(hello.NodeId, _nodeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isAlive = await ProbePeerAsync(hello.Host, hello.Port, ct);
        if (!isAlive)
            return;

        var seenAt = DateTime.UtcNow;
        var next = new PeerNode(hello.NodeId, hello.DisplayName ?? hello.NodeId, hello.Host, hello.Port, seenAt);
        var isNewPeer = false;

        _peers.AddOrUpdate(
            hello.NodeId,
            _ =>
            {
                isNewPeer = true;
                return next;
            },
            (_, existing) => existing with
            {
                DisplayName = string.IsNullOrWhiteSpace(hello.DisplayName) ? existing.DisplayName : hello.DisplayName,
                Host = hello.Host,
                Port = hello.Port,
                LastSeen = seenAt,
            });

        if (!isNewPeer)
            return;

        _logger.LogInformation(
            "[P2P] 发现新节点。peerNodeId={PeerNodeId} endpoint=http://{Host}:{Port}",
            next.NodeId,
            next.Host,
            next.Port);
        PeerDiscovered?.Invoke(this, next);
    }

    /// <summary>
    /// 通过 HTTP ping 检查对端是否可达。
    /// </summary>
    private async Task<bool> ProbePeerAsync(string host, int port, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = PingTimeout;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(PingTimeout);

        try
        {
            var response = await client.GetAsync($"http://{host}:{port}/api/p2p/ping", linkedCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "[P2P] 对端探活失败。host={Host} port={Port}", host, port);
            return false;
        }
    }

    /// <summary>
    /// 清理超时未更新心跳的节点。
    /// </summary>
    private void CleanupOfflinePeers()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _peers)
        {
            if (now - item.Value.LastSeen <= OfflineThreshold)
                continue;

            if (!_peers.TryRemove(item.Key, out var removed))
                continue;

            _logger.LogInformation(
                "[P2P] 节点离线。peerNodeId={PeerNodeId} lastSeen={LastSeen:o}",
                removed.NodeId,
                removed.LastSeen);
            PeerLost?.Invoke(this, removed);
        }
    }

    private sealed record HelloPacket
    {
        public string Type { get; init; } = string.Empty;
        public string NodeId { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; }
        public DateTime Timestamp { get; init; }
    }
}

/// <summary>
/// P2P 节点身份解析工具。
/// </summary>
internal static class P2pNodeIdentity
{
    /// <summary>
    /// 解析节点 ID。
    /// </summary>
    public static string ResolveNodeId(IConfiguration configuration) =>
        configuration["Pudding:P2P:NodeId"]
        ?? configuration["Pudding:NodeId"]
        ?? $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>
    /// 解析节点展示名。
    /// </summary>
    public static string ResolveDisplayName(IConfiguration configuration) =>
        configuration["Pudding:P2P:DisplayName"]
        ?? configuration["Pudding:DisplayName"]
        ?? Environment.MachineName;

    /// <summary>
    /// 解析服务端口。
    /// </summary>
    public static int ResolvePort(IConfiguration configuration)
    {
        if (int.TryParse(configuration["Pudding:P2P:Port"], out var configuredPort) && configuredPort > 0)
            return configuredPort;

        var urls = configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            var segments = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                if (Uri.TryCreate(segment, UriKind.Absolute, out var uri) && uri.Port > 0)
                    return uri.Port;
            }
        }

        return 8080;
    }

    /// <summary>
    /// 解析本机可广播地址（IPv4）。
    /// </summary>
    public static string ResolveHostAddress()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var nic in interfaces)
            {
                var unicast = nic.GetIPProperties().UnicastAddresses;
                var ipv4 = unicast.FirstOrDefault(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(a.Address)
                    && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));

                if (ipv4 is not null)
                    return ipv4.Address.ToString();
            }
        }
        catch
        {
            // 回退逻辑在下方统一处理。
        }

        return "127.0.0.1";
    }
}