using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// 优先级事件队列表 — 持久化存储入站事件，防丢失。
/// </summary>
public class EventQueueEntity
{
    [Key]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>优先级：0=Normal / 5=Important / 10=Urgent</summary>
    public int Priority { get; set; }

    [MaxLength(128)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>来源类型：cron / mqtt / webhook / email / internal</summary>
    [MaxLength(32)]
    public string? SourceType { get; set; }

    [MaxLength(128)]
    public string? SourceId { get; set; }

    /// <summary>目标 Workspace</summary>
    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>目标 Agent（可选）</summary>
    [MaxLength(64)]
    public string? AgentId { get; set; }

    /// <summary>事件负载 JSON</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>隔离模式：mainline / isolated</summary>
    [MaxLength(16)]
    public string IsolationMode { get; set; } = "isolated";

    /// <summary>状态：pending / processing / completed / failed</summary>
    [MaxLength(16)]
    public string Status { get; set; } = "pending";

    /// <summary>Unix 毫秒时间戳</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public long? StartedAt { get; set; }
    public long? CompletedAt { get; set; }
    public int RetryCount { get; set; }

    /// <summary>失败时的错误信息</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 事件诊断日志 — 记录每个事件的全生命周期阶段耗时。
/// </summary>
public class EventDiagnosticLogEntity
{
    [Key]
    [MaxLength(32)]
    public string LogId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联的事件 ID</summary>
    [MaxLength(32)]
    public string EventId { get; set; } = string.Empty;

    /// <summary>阶段：received / preprocessed / enqueued / dequeued / dispatched / checkpoint_saved / completed / failed</summary>
    [MaxLength(32)]
    public string Stage { get; set; } = string.Empty;

    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>阶段详细信息</summary>
    public string? Detail { get; set; }

    /// <summary>阶段耗时（毫秒），从上一阶段到当前阶段</summary>
    public int DurationMs { get; set; }
}

/// <summary>
/// Agent 现场检查点 — 保存打断时的执行现场。
/// </summary>
public class AgentCheckpointEntity
{
    [Key]
    [MaxLength(32)]
    public string CheckpointId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>调用栈 JSON（当前执行位置）</summary>
    public string CallStack { get; set; } = "{}";

    /// <summary>未完成的工具调用 JSON</summary>
    public string? PendingTools { get; set; }

    /// <summary>上下文快照 JSON（最近 N 条消息摘要）</summary>
    public string? ContextSnapshot { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [MaxLength(32)]
    public string Status { get; set; } = "active"; // active / restored / expired
}

/// <summary>
/// 事件订阅 — Agent 主动订阅感兴趣的事件类型。
/// </summary>
public class EventSubscriptionEntity
{
    [Key]
    [MaxLength(32)]
    public string SubscriptionId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(64)]
    public string AgentId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>事件类型匹配模式，如 "mqtt.sensor.*"</summary>
    [MaxLength(256)]
    public string EventTypePattern { get; set; } = string.Empty;

    /// <summary>过滤表达式，如 "priority>=5"</summary>
    public string? FilterExpression { get; set; }

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [MaxLength(16)]
    public string Status { get; set; } = "active"; // active / cancelled
}
