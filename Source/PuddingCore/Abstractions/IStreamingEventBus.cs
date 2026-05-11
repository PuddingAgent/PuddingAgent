namespace PuddingCode.Abstractions;

/// <summary>
/// 流式事件总线：Agent/Subconscious/Tool/Skill/MCP 任意组件可发布事件，
/// 前端通过 SSE 实时接收。抽象接口支持未来扩展新的事件类型和路由策略。
/// </summary>
public interface IStreamingEventBus
{
    /// <summary>发布流式事件到所有订阅者（通常是 SSE 连接）。</summary>
    Task EmitAsync(StreamingEvent ev, CancellationToken ct = default);
}

/// <summary>流式事件数据模型。</summary>
public sealed record StreamingEvent
{
    /// <summary>事件类型，命名规范：{category}.{operation}，如 agent.thinking。</summary>
    public string Type { get; init; } = "";

    /// <summary>事件负载，任意可序列化对象。</summary>
    public object? Data { get; init; }

    /// <summary>时间戳（前端排序用）。</summary>
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>流式事件类型常量（推荐使用，避免硬编码字符串）。</summary>
public static class StreamingEventTypes
{
    public const string AgentThinking = "agent.thinking";
    public const string AgentToolCall = "agent.tool_call";
    public const string AgentToolResult = "agent.tool_result";
    public const string AgentDelta = "agent.delta";
    public const string SubconsciousLoad = "subconscious.load";
    public const string SubconsciousThink = "subconscious.think";
    public const string SubconsciousDone = "subconscious.done";
}
