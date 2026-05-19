using System.Collections.Frozen;

namespace PuddingCode.Events;

/// <summary>
/// 事件 Schema 的作用域。
/// Internal: 系统内部事件（subagent.run.*、agent.*、cron.*、connector.*、llm_gateway/*、tool_runner/*）
/// SessionFrame: 会话帧事件（delta、thinking、tool_call、tool_result、done、error 等面向前端的 SSE 帧）
/// </summary>
public enum EventSchemaScope
{
    Internal,
    SessionFrame
}

/// <summary>
/// 事件 Schema 定义 — 描述单个事件类型的名称、版本、类别、作用域、必填/可选字段。
/// </summary>
public sealed record EventSchemaDefinition(
    string EventType,
    int CurrentVersion,
    string Category,
    string Description,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string>? OptionalFields = null,
    EventSchemaScope Scope = EventSchemaScope.Internal
);

/// <summary>
/// Schema 兼容性检查结果。
/// </summary>
public sealed record SchemaCompatibilityResult(
    bool IsCompatible,
    string? BreakingChangeDescription = null
);

/// <summary>
/// 集中注册所有事件类型及其 schema，支持版本管理和兼容性检查。
/// 
/// 所有 InternalEvent 在创建时应通过 GetSchemaVersion() 获取当前版本，
/// 在反序列化时应通过 CheckCompatibility() 验证版本兼容性。
/// 
/// Schema 以 (Scope, EventType) 联合唯一，不同 scope 允许相同 eventType。
/// </summary>
public static class EventSchemaRegistry
{
    /// <summary>所有已注册的事件 schema，key 格式为 "{scope}::{eventType}"。</summary>
    public static readonly IReadOnlyDictionary<string, EventSchemaDefinition> Schemas;

    static EventSchemaRegistry()
    {
        var dict = new Dictionary<string, EventSchemaDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in BuildAllSchemas())
        {
            var key = MakeKey(def.Scope, def.EventType);
            if (!dict.TryAdd(key, def))
                throw new InvalidOperationException(
                    $"Duplicate event type '{def.EventType}' in scope '{def.Scope}' in EventSchemaRegistry");
        }

        Schemas = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>构造 (Scope, EventType) 联合唯一键。</summary>
    private static string MakeKey(EventSchemaScope scope, string eventType)
        => $"{scope}::{eventType}";

    /// <summary>验证事件类型是否已在 Internal scope 注册表中。</summary>
    public static bool IsValidEventType(string eventType)
    {
        return Schemas.ContainsKey(MakeKey(EventSchemaScope.Internal, eventType));
    }

    /// <summary>获取事件的当前 schema 版本（默认查询 Internal scope）。未注册类型返回 1。</summary>
    public static int GetSchemaVersion(string eventType, EventSchemaScope scope = EventSchemaScope.Internal)
    {
        return Schemas.TryGetValue(MakeKey(scope, eventType), out var def) ? def.CurrentVersion : 1;
    }

    /// <summary>
    /// 检查事件 schema 版本兼容性（默认查询 Internal scope）。
    /// 规则：同版本兼容；旧版本（fromVersion &lt; toVersion）兼容但降级；fromVersion > toVersion 认为不兼容。
    /// </summary>
    public static SchemaCompatibilityResult CheckCompatibility(string eventType, int fromVersion, int toVersion, EventSchemaScope scope = EventSchemaScope.Internal)
    {
        if (fromVersion == toVersion)
            return new SchemaCompatibilityResult(true);

        if (!Schemas.TryGetValue(MakeKey(scope, eventType), out var def))
        {
            return new SchemaCompatibilityResult(false,
                $"Unknown event type '{eventType}' in scope '{scope}'. Cannot verify compatibility.");
        }

        if (fromVersion > toVersion)
        {
            return new SchemaCompatibilityResult(false,
                $"Event type '{eventType}' has version {fromVersion} but registry requires version {toVersion}. " +
                $"Downgrade not supported.");
        }

        // fromVersion < toVersion: forward compatible (older event processed by newer code)
        return new SchemaCompatibilityResult(true);
    }

    /// <summary>构建所有已知事件 schema 定义，按 scope 分配。</summary>
    private static IEnumerable<EventSchemaDefinition> BuildAllSchemas()
    {
        // ════════════════════════════════════════════════════
        // SessionFrame: 面向前端的 SSE 帧事件
        // ════════════════════════════════════════════════════

        // 内容层
        yield return new EventSchemaDefinition("delta", 1, "session",
            "回复文本增量", ["session_id", "data"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("thinking", 1, "session",
            "思维链增量", ["session_id", "data"],
            Scope: EventSchemaScope.SessionFrame);

        // 工具层
        yield return new EventSchemaDefinition("tool_call", 1, "session",
            "工具调用开始", ["session_id", "tool_name"], ["tool_call_id", "arguments"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("tool_result", 1, "session",
            "工具调用结果", ["session_id", "tool_name"], ["tool_call_id", "result", "error"],
            Scope: EventSchemaScope.SessionFrame);

        // 子代理层（SessionFrame）
        yield return new EventSchemaDefinition("subagent.spawned", 1, "session",
            "异步子代理已创建", ["session_id", "sub_agent_id"], ["template", "task"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("subagent.delta", 1, "session",
            "同步子代理文本增量", ["session_id", "sub_agent_id", "data"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("subagent.thinking", 1, "session",
            "同步子代理思维链", ["session_id", "sub_agent_id", "data"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("subagent.tool_call", 1, "session",
            "同步子代理工具调用", ["session_id", "sub_agent_id", "tool_name"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("subagent.tool_result", 1, "session",
            "同步子代理工具结果", ["session_id", "sub_agent_id", "tool_name"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("subagent.completed", 1, "session",
            "任意子代理完成", ["session_id", "sub_agent_id", "success"], ["reply", "error"],
            Scope: EventSchemaScope.SessionFrame);

        // 生命周期层
        yield return new EventSchemaDefinition("done", 1, "session",
            "正常结束", ["session_id"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("error", 1, "session",
            "错误终止", ["session_id"], ["error_message"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("cancelled", 1, "session",
            "用户取消", ["session_id"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("session.closed", 1, "session",
            "会话完全关闭", ["session_id"],
            Scope: EventSchemaScope.SessionFrame);

        // 元数据层
        yield return new EventSchemaDefinition("metadata", 1, "session",
            "会话元数据", ["session_id"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("usage", 1, "session",
            "Token 用量", ["session_id"], ["input_tokens", "output_tokens"],
            Scope: EventSchemaScope.SessionFrame);

        // 系统通知层
        yield return new EventSchemaDefinition("notification", 1, "session",
            "系统级通知", ["session_id", "message"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("notification.sub_agent_completed", 1, "session",
            "子代理通知类型", ["session_id", "sub_agent_id"],
            Scope: EventSchemaScope.SessionFrame);

        // SSE 额外事件
        yield return new EventSchemaDefinition("terminal", 1, "session",
            "终端代理输出", ["session_id", "data"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("context", 1, "session",
            "上下文层 Token 占比", ["session_id"],
            Scope: EventSchemaScope.SessionFrame);
        yield return new EventSchemaDefinition("step", 1, "session",
            "状态转换（向后兼容）", ["session_id"],
            Scope: EventSchemaScope.SessionFrame);

        // ════════════════════════════════════════════════════
        // Internal: 系统内部事件
        // ════════════════════════════════════════════════════

        // Agent 执行事件
        yield return new EventSchemaDefinition("agent.started", 1, "agent",
            "Agent 开始执行", ["session_id", "agent_id"]);
        yield return new EventSchemaDefinition("agent.completed", 1, "agent",
            "Agent 执行完成", ["session_id", "agent_id"], ["reply", "usage"]);
        yield return new EventSchemaDefinition("agent.failed", 1, "agent",
            "Agent 执行失败", ["session_id", "agent_id", "error"]);
        yield return new EventSchemaDefinition("agent.cancelled", 1, "agent",
            "Agent 执行被取消", ["session_id", "agent_id"]);
        yield return new EventSchemaDefinition("agent.sub_completed", 1, "agent",
            "子代理完成通知", ["session_id", "agent_id", "sub_agent_id", "success"], ["reply", "error"]);
        yield return new EventSchemaDefinition("agent.permission_required", 1, "agent",
            "Agent 请求权限", ["session_id", "agent_id", "permission"]);
        yield return new EventSchemaDefinition("agent.thinking", 1, "agent",
            "Agent 思维链（流式）", ["session_id", "data"]);
        yield return new EventSchemaDefinition("agent.tool_call", 1, "agent",
            "Agent 工具调用（流式）", ["session_id", "tool_name"]);
        yield return new EventSchemaDefinition("agent.tool_result", 1, "agent",
            "Agent 工具结果（流式）", ["session_id", "tool_name"]);
        yield return new EventSchemaDefinition("agent.delta", 1, "agent",
            "Agent 文本增量（流式）", ["session_id", "data"]);

        // Cron 事件
        yield return new EventSchemaDefinition("cron.trigger", 1, "cron",
            "定时任务触发", ["session_id", "job_name", "prompt"]);
        yield return new EventSchemaDefinition("cron.completed", 1, "cron",
            "定时任务完成", ["session_id", "job_name"]);
        yield return new EventSchemaDefinition("cron.failed", 1, "cron",
            "定时任务失败", ["session_id", "job_name", "error"]);

        // Connector 事件
        yield return new EventSchemaDefinition("connector.message", 1, "connector",
            "连接器入站消息", ["channel_id", "channel_type", "user_external_id", "message_text"]);
        yield return new EventSchemaDefinition("connector.error", 1, "connector",
            "连接器错误", ["channel_id", "error"]);
        yield return new EventSchemaDefinition("connector.connected", 1, "connector",
            "连接器已连接", ["channel_id", "channel_type"]);
        yield return new EventSchemaDefinition("connector.disconnected", 1, "connector",
            "连接器已断开", ["channel_id", "channel_type"]);

        // 子代理运行事件（ADR-021-D: subagent.run.* 前缀）
        yield return new EventSchemaDefinition("subagent.run.created", 1, "subagent",
            "子代理已创建", ["parent_session_id", "sub_agent_id", "template"]);
        yield return new EventSchemaDefinition("subagent.run.started", 1, "subagent",
            "子代理开始执行", ["parent_session_id", "sub_agent_id"]);
        yield return new EventSchemaDefinition("subagent.run.context_assembled", 1, "subagent",
            "子代理上下文已装配", ["parent_session_id", "sub_agent_id"]);
        yield return new EventSchemaDefinition("subagent.run.completed", 1, "subagent",
            "子代理已完成", ["parent_session_id", "sub_agent_id", "success"], ["reply", "error"]);
        yield return new EventSchemaDefinition("subagent.run.failed", 1, "subagent",
            "子代理已失败", ["parent_session_id", "sub_agent_id", "error"]);
        yield return new EventSchemaDefinition("subagent.run.cancelled", 1, "subagent",
            "子代理已取消", ["parent_session_id", "sub_agent_id"]);

        // LLM 事件
        yield return new EventSchemaDefinition("llm_gateway/chat", 1, "llm",
            "LLM 同步聊天请求", ["session_id", "model", "messages"]);
        yield return new EventSchemaDefinition("llm_gateway/chat_stream", 1, "llm",
            "LLM 流式聊天请求", ["session_id", "model", "messages"]);

        // Tool 事件
        yield return new EventSchemaDefinition("tool_runner/execute_tool", 1, "tool",
            "工具执行请求", ["session_id", "tool_name"]);
    }
}
