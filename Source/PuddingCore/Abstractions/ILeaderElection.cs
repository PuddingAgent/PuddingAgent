using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Leader 选举接口。用于 P2P 分布式蜂群中的节点领导选举。
/// </summary>
/// <remarks>
/// Phase 3 stub - P2P distributed swarm feature.
/// Current implementation throws NotImplementedException.
/// </remarks>
public interface ILeaderElection
{
    /// <summary>
    /// 从候选节点列表中选举 Leader。
    /// </summary>
    /// <param name="candidates">候选节点列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>当选 Leader 的节点 ID</returns>
    /// <remarks>Phase 3 stub - throw NotImplementedException</remarks>
    Task<string> ElectLeaderAsync(IReadOnlyList<SwarmNode> candidates, CancellationToken ct = default);

    /// <summary>
    /// 检查当前 Leader 是否存活（心跳检测）。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>Leader 存活返回 true，否则返回 false</returns>
    /// <remarks>Phase 3 stub - throw NotImplementedException</remarks>
    Task<bool> IsCurrentLeaderAliveAsync(CancellationToken ct = default);
}
