namespace PuddingCode.Platform;

/// <summary>
/// 连接器主接口 — 管理一个外部协议通道的完整生命周期。
/// 替代旧 IPuddingGatewayAdapter，支持双向通道（接收/发送/管理）。
/// </summary>
public interface IPuddingConnector
{
    /// <summary>连接器描述符。</summary>
    ConnectorDescriptor Descriptor { get; }

    /// <summary>启动连接器，建立通道连接并开始监听。</summary>
    Task StartAsync(ConnectorContext context, CancellationToken ct = default);

    /// <summary>停止连接器，关闭通道连接。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>向外部通道发送消息。</summary>
    Task SendAsync(ConnectorMessage message, CancellationToken ct = default);

    /// <summary>对外部通道执行操作（如移动邮件、查询 Topic 列表等）。</summary>
    Task<ConnectorOperationResult> OperateAsync(
        string operation, Dictionary<string, string>? parameters = null,
        CancellationToken ct = default);

    /// <summary>获取连接器当前状态与诊断信息。</summary>
    Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default);
}

/// <summary>连接器能力声明。</summary>
public enum ConnectorCapability
{
    Receive,  // 可接收外部事件
    Send,     // 可向外发送消息
    Manage,   // 可管理外部通道（如邮箱文件夹操作）
    Stream    // 支持流式数据（如 MQTT 持续订阅）
}

/// <summary>连接器描述符。</summary>
public sealed record ConnectorDescriptor
{
    public required string ConnectorId { get; init; }
    public required string ConnectorType { get; init; }
    public required string Protocol { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

/// <summary>连接器运行上下文。</summary>
public sealed class ConnectorContext
{
    /// <summary>入站事件回调 — 连接器收到外部消息时调用。</summary>
    public required Func<PuddingIngressEnvelope, CancellationToken, Task> OnEventReceived { get; init; }

    /// <summary>连接器日志。</summary>
    public required Action<string> Log { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>连接器出站消息。</summary>
public sealed record ConnectorMessage
{
    /// <summary>目标地址（如邮箱地址、MQTT Topic、Webhook URL）。</summary>
    public required string Target { get; init; }

    /// <summary>消息内容。</summary>
    public required string Content { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>连接器操作结果。</summary>
public sealed record ConnectorOperationResult
{
    public bool Success { get; init; }
    public string? Data { get; init; }
    public string? Error { get; init; }
}

/// <summary>连接器诊断信息。</summary>
public sealed record ConnectorDiagnostics
{
    public string Status { get; init; } = "unknown";
    public long MessagesReceived { get; init; }
    public long MessagesSent { get; init; }
    public long Errors { get; init; }
    public DateTimeOffset? LastReceiveTime { get; init; }
    public DateTimeOffset? LastErrorTime { get; init; }
    public string? LastError { get; init; }
}
