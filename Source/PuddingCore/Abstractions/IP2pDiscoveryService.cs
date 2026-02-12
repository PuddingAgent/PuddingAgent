namespace PuddingCode.Abstractions;

/// <summary>
/// P2P 节点发现服务抽象。
/// </summary>
public interface IP2pDiscoveryService
{
    /// <summary>
    /// 启动节点发现。
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// 停止节点发现。
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 获取当前已发现的在线对等节点。
    /// </summary>
    IReadOnlyList<PeerNode> GetPeers();

    /// <summary>
    /// 发现新节点事件。
    /// </summary>
    event EventHandler<PeerNode>? PeerDiscovered;

    /// <summary>
    /// 节点离线事件。
    /// </summary>
    event EventHandler<PeerNode>? PeerLost;
}

/// <summary>
/// 对等节点信息。
/// </summary>
/// <param name="NodeId">节点唯一 ID。</param>
/// <param name="DisplayName">节点展示名称。</param>
/// <param name="Host">节点主机地址。</param>
/// <param name="Port">节点端口。</param>
/// <param name="LastSeen">最后一次心跳时间（UTC）。</param>
public sealed record PeerNode(string NodeId, string DisplayName, string Host, int Port, DateTime LastSeen);