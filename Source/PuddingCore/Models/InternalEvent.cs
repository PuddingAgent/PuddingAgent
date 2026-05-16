namespace PuddingCode.Models;

/// <summary>
/// 事件优先级：Normal=0 顺序处理 / Important=5 插队保留现场 / Urgent=10 即时打断
/// </summary>
public enum EventPriorityLevel
{
    Normal = 0,
    Important = 5,
    Urgent = 10,
}

/// <summary>
/// 事件隔离模式：Mainline 合并到当前会话 / Isolated 分支执行后丢弃
/// </summary>
public enum EventIsolationMode
{
    Mainline,
    Isolated,
}

/// <summary>
/// 事件来源描述。
/// </summary>
public sealed record EventSource
{
    /// <summary>来源类型：cron / mqtt / webhook / email / internal</summary>
    public string SourceType { get; init; } = "internal";

    /// <summary>来源 ID（如 CronJob Name、MQTT topic）</summary>
    public string? SourceId { get; init; }

    /// <summary>关联的 Connector ID（外部来源时有值）</summary>
    public string? ConnectorId { get; init; }
}

/// <summary>
/// 统一的内部事件模型。贯穿 Connector → Preprocessor → Queue → Dispatcher → Handler 全链路。
/// </summary>
public sealed record InternalEvent
{
    /// <summary>事件唯一 ID</summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>事件类型，命名规范 {category}.{operation}，如 cron.trigger / mqtt.sensor.motion</summary>
    public string Type { get; init; } = "";

    /// <summary>优先级</summary>
    public EventPriorityLevel Priority { get; init; } = EventPriorityLevel.Normal;

    /// <summary>隔离模式</summary>
    public EventIsolationMode Isolation { get; init; } = EventIsolationMode.Isolated;

    /// <summary>事件来源</summary>
    public EventSource Source { get; init; } = new();

    /// <summary>关联会话（Mainline 模式时有值）</summary>
    public string? SessionId { get; init; }

    /// <summary>目标 Workspace</summary>
    public string WorkspaceId { get; init; } = "default";

    /// <summary>目标 Agent</summary>
    public string? AgentId { get; init; }

    /// <summary>事件负载（任意 JSON 可序列化对象）</summary>
    public object? Payload { get; init; }

    /// <summary>创建时间（Unix ms）</summary>
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>扩展元数据</summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Connector 入站的原始事件（未经预处理）。
/// </summary>
public sealed record RawEvent
{
    public string RawEventId { get; init; } = Guid.NewGuid().ToString("N");
    public string Type { get; init; } = "";
    public EventSource Source { get; init; } = new();
    public string WorkspaceId { get; init; } = "default";
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
    public object? Payload { get; init; }
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// 预处理后的事件（去重/批处理后）。
/// </summary>
public sealed record ProcessedEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public string Type { get; init; } = "";
    public EventSource Source { get; init; } = new();
    public string WorkspaceId { get; init; } = "default";
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
    public object? Payload { get; init; }
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>合并后的原始事件 ID 列表（批处理场景）</summary>
    public List<string>? MergedRawEventIds { get; init; }

    public int MergeCount { get; init; } = 1;
}

/// <summary>
/// 持久队列中的事件条目。
/// </summary>
public sealed record QueuedEvent
{
    public string Id { get; init; } = "";
    public int Priority { get; init; }
    public string EventType { get; init; } = "";
    public string? SourceType { get; init; }
    public string? SourceId { get; init; }
    public string? SessionId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? AgentId { get; init; }
    public string Payload { get; init; } = "{}";
    public string Status { get; init; } = "pending";
    public long CreatedAt { get; init; }
    public long? StartedAt { get; init; }
    public long? CompletedAt { get; init; }
    public int RetryCount { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 队列统计信息。
/// </summary>
public sealed record QueueStats
{
    public int NormalPending { get; init; }
    public int ImportantPending { get; init; }
    public int UrgentPending { get; init; }
    public int Processing { get; init; }
    public int TotalPending => NormalPending + ImportantPending + UrgentPending;
}

/// <summary>
/// Agent 现场保存点。
/// </summary>
public sealed record AgentCheckpoint
{
    public string CheckpointId { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string CallStack { get; init; } = "{}";
    public string? PendingTools { get; init; }
    public string? ContextSnapshot { get; init; }
    public long CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Status { get; init; } = "active";
}

/// <summary>
/// 事件订阅描述。
/// </summary>
public sealed record EventSubscription
{
    public string SubscriptionId { get; init; } = Guid.NewGuid().ToString("N");
    public string AgentId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";

    /// <summary>事件类型匹配模式，支持 * 通配符，如 "mqtt.sensor.*"</summary>
    public string EventTypePattern { get; init; } = "";

    /// <summary>可选过滤表达式，如 "priority>=5&sourceType=mqtt"</summary>
    public string? FilterExpression { get; init; }

    public long CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Status { get; init; } = "active";
}
