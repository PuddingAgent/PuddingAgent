namespace PuddingCode.Platform;

/// <summary>
/// 会话事件类型常量 — 从 SSE event 字段扩展到会话事件日志的完整事件分类。
/// 
/// 用途：
///   · SessionEventLog 的 event_type 列
///   · 前端 AdminChatStreamEvent 联合类型
///   · ServerSentEventFrame.Event 字段
/// 
/// 关联 ADR：Docs/07架构/14消息管线与终端代理与前端优化ADR.md（SSE 基础类型）
///           Docs/07架构/16会话状态层与客户端解耦ADR.md（本次扩展）
/// </summary>
public static class SessionEventTypes
{
    // ════════════════════════════════════════════════════════
    // 内容层 — 来自 ADR-014-A（SSE v2）
    // ════════════════════════════════════════════════════════
    public const string Delta = "delta";
    public const string Thinking = "thinking";

    // ════════════════════════════════════════════════════════
    // 工具层 — 来自 ADR-014-A（SSE v2）
    // ════════════════════════════════════════════════════════
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";

    // ════════════════════════════════════════════════════════
    // 子代理层 — ADR-016 新增
    // ════════════════════════════════════════════════════════

    /// <summary>异步子代理已创建 (subAgentId, template, task)</summary>
    public const string SubAgentSpawned = "subagent.spawned";

    /// <summary>同步子代理文本增量</summary>
    public const string SubAgentDelta = "subagent.delta";

    /// <summary>同步子代理思维链</summary>
    public const string SubAgentThinking = "subagent.thinking";

    /// <summary>同步子代理工具调用</summary>
    public const string SubAgentToolCall = "subagent.tool_call";

    /// <summary>同步子代理工具结果</summary>
    public const string SubAgentToolResult = "subagent.tool_result";

    /// <summary>任意子代理完成 (sync/async 统一，subAgentId, success, reply, error)</summary>
    public const string SubAgentCompleted = "subagent.completed";

    // ════════════════════════════════════════════════════════
    // 生命周期层
    // ════════════════════════════════════════════════════════
    public const string Done = "done";
    public const string Error = "error";
    public const string Cancelled = "cancelled";

    /// <summary>会话完全关闭（所有子代理完成，无更多事件）— ADR-016 新增</summary>
    public const string SessionClosed = "session.closed";

    // ════════════════════════════════════════════════════════
    // 元数据层
    // ════════════════════════════════════════════════════════
    public const string Metadata = "metadata";
    public const string Usage = "usage";

    // ════════════════════════════════════════════════════════
    // 系统通知层 — ADR-016 新增
    // ════════════════════════════════════════════════════════

    /// <summary>系统级通知（注入到 Timeline，不作为"用户/Agent消息"）</summary>
    public const string Notification = "notification";

    /// <summary>子代理通知类型（工作区 SSE 使用）</summary>
    public const string NotificationSubAgentCompleted = "notification.sub_agent_completed";
}
