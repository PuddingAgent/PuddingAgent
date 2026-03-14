using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 蜂群通信通道。本地模式通过文件系统实现，分布式模式通过 P2P 网络实现。
/// </summary>
public interface ISwarmTransport
{
    /// <summary>发送消息给指定节点</summary>
    /// <param name="targetNodeId">目标节点 ID</param>
    /// <param name="message">要发送的消息</param>
    /// <param name="ct">取消令牌</param>
    Task SendAsync(string targetNodeId, SwarmMessage message, CancellationToken ct = default);

    /// <summary>广播消息给所有节点</summary>
    /// <param name="message">要广播的消息</param>
    /// <param name="ct">取消令牌</param>
    Task BroadcastAsync(SwarmMessage message, CancellationToken ct = default);

    /// <summary>接收消息流</summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>异步消息枚举器</returns>
    IAsyncEnumerable<SwarmMessage> ReceiveAsync(CancellationToken ct = default);
}
