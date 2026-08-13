namespace PuddingCode.Platform;

/// <summary>
/// 飞书回信路由解析端口。
/// 工具（send_message / send_image 等）通过当前执行的 CommandId 解析受信任的
/// 飞书连接器回信路由。实现位于 PuddingPlatform（读取
/// ChatExecutionCommands.MetadataJson），使 PuddingRuntime 工具无需反向引用
/// PuddingPlatform。
/// </summary>
public interface IGatewayCommandRouteReader
{
    /// <summary>
    /// 按 CommandId 读取执行命令的飞书网关回信路由；命令不存在时返回 null。
    /// </summary>
    Task<GatewayCommandRoute?> GetAsync(
        string commandId,
        CancellationToken ct = default);

    /// <summary>
    /// 主动发送场景（无 CommandId，如心跳/网页端）：按 Agent + 工作区查找最近一条
    /// 飞书网关入口命令（IsGatewayIngress=true 且 ChannelType=feishu）的稳定回信路由，
    /// 用于把主动消息投递到用户最近一次与该 Agent 对话的飞书单聊会话。
    /// 找不到（该 Agent 从未收到过飞书消息）返回 null。
    /// </summary>
    Task<GatewayCommandRoute?> FindRecentFeishuRouteAsync(
        string agentInstanceId,
        string workspaceId,
        CancellationToken ct = default);
}

/// <summary>
/// 一次飞书网关入口命令（IsGatewayIngress=true 且 ChannelType=feishu）的
/// 稳定回信路由信息。供工具构造 Kind=Connector 的 MessageEnvelope 使用。
/// </summary>
public sealed record GatewayCommandRoute
{
    public required string CommandId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ConversationId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? TurnId { get; init; }
    public bool IsGatewayIngress { get; init; }
    public string? ChannelType { get; init; }
    public string? ConnectorId { get; init; }
    public string? ExternalConversationId { get; init; }
    public string? ExternalMessageId { get; init; }

    /// <summary>命令的完整 MetadataJson 反序列化结果（含全部 gateway_* 键）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
