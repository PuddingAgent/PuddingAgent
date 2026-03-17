namespace PuddingCode.Platform;

/// <summary>Gateway 适配器主接口——每个外部渠道实现此接口。</summary>
public interface IPuddingGatewayAdapter
{
    /// <summary>适配器描述符。</summary>
    GatewayAdapterDescriptor Descriptor { get; }

    /// <summary>初始化并建立事件订阅。</summary>
    Task StartAsync(GatewayAdapterContext context, CancellationToken ct = default);

    /// <summary>停止适配器。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>向外部系统回写消息。</summary>
    Task PublishAsync(PuddingEgressEnvelope envelope, CancellationToken ct = default);
}

/// <summary>适配器描述符。</summary>
public sealed record GatewayAdapterDescriptor
{
    public required string AdapterId { get; init; }
    public required string AdapterType { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> SupportedChannelTypes { get; init; } = [];
}

/// <summary>适配器运行上下文。</summary>
public sealed class GatewayAdapterContext
{
    /// <summary>入站事件回调——适配器收到消息后调用。</summary>
    public required Func<PuddingIngressEnvelope, CancellationToken, Task> OnEventReceived { get; init; }

    /// <summary>日志记录。</summary>
    public required Action<string> Log { get; init; }

    /// <summary>取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>适配器运行状态。</summary>
public enum AdapterStatus
{
    Registered,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}

/// <summary>适配器运行时信息。</summary>
public sealed record AdapterRuntimeInfo
{
    public required string AdapterId { get; init; }
    public required AdapterStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? FaultReason { get; init; }
}
