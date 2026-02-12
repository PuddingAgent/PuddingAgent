namespace PuddingCode.Models;

/// <summary>
/// 蜂群消息。用于 Agent 之间的通信。
/// </summary>
/// <param name="Id">消息唯一 ID</param>
/// <param name="From">发送者节点 ID</param>
/// <param name="To">目标节点 ID（广播消息为 null）</param>
/// <param name="Type">消息类型</param>
/// <param name="Content">消息内容</param>
/// <param name="Timestamp">消息创建时间</param>
public sealed record SwarmMessage(
    string Id,
    string From,
    string? To,
    string Type,
    string Content,
    DateTimeOffset Timestamp,
    SwarmMessagePriority Priority = SwarmMessagePriority.Normal,
    Dictionary<string, string>? Metadata = null
);
