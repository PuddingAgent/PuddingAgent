namespace PuddingCode.Models;

/// <summary>
/// 蜂群网络中的一个节点。
/// </summary>
/// <remarks>
/// Phase 3 feature - stub for P2P distributed swarm.
/// Current implementation is a placeholder for future network functionality.
/// </remarks>
public sealed record SwarmNode
{
    /// <summary>全局唯一节点 ID（Ed25519 公钥派生）。</summary>
    public required string NodeId { get; init; }

    /// <summary>节点角色。</summary>
    public required SwarmNodeRole Role { get; set; }

    /// <summary>可达地址列表（可能有多个：LAN IP、公网 IP、中继地址）。</summary>
    public required IReadOnlyList<string> Addresses { get; init; }

    /// <summary>节点能力标签（可用模型、算力等级等）。</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

/// <summary>
/// 蜂群节点角色枚举。
/// </summary>
/// <remarks>
/// Phase 3 feature - stub for P2P distributed swarm.
/// </remarks>
public enum SwarmNodeRole
{
    /// <summary>Leader 节点，负责协调和任务分配。</summary>
    Leader,

    /// <summary>Builder 节点，负责实现代码。</summary>
    Builder,

    /// <summary>QA 节点，负责测试验证。</summary>
    QA,

    /// <summary>Docs 节点，负责文档生成。</summary>
    Docs,

    /// <summary>Relay 节点，负责中继转发消息。</summary>
    Relay
}
