using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 事件处理者 — 事件系统与外部世界的唯一边界。
/// 
/// 事件系统是纯管道：接收 InternalEvent → 预处理 → 优先级入队 → 出队 → 分发到 IEventHandler。
/// 事件系统不知道 Cron、Connector、AgentExecutionService 的存在。
/// 所有事件消费者均通过实现此接口接入。
/// 
/// 设计原则：
///   · 万物皆事件 — 用户消息、Cron 触发、连接器入站、Agent 间通信、系统信号，都是事件。
///   · 事件系统是基石 — 消息系统、定时系统、通知系统均建立在事件系统之上。
///   · 显式订阅：Agent 通过 subscribe_events 工具主动注册
///   · 隐式订阅：Agent 异步执行长耗时命令/子代理时，自动注册 completion 事件回调
/// </summary>
public interface IEventHandler
{
    /// <summary>
    /// 此处理者关注的通配符模式，如 "cron.*", "agent.*", "message.*"。
    /// 用于 EventDispatcher 的快速匹配路由。
    /// </summary>
    string EventTypePattern { get; }

    /// <summary>
    /// 处理一个事件。
    /// 返回 true 表示成功，false 表示失败（将触发重试或死信）。
    /// </summary>
    Task<bool> HandleAsync(InternalEvent evt, CancellationToken ct);

    /// <summary>
    /// 是否支持被更高优先级事件打断。
    /// Urgent 事件入队时，EventDispatcher 会检查当前正在执行的处理者，
    /// 若 SupportsInterruption == true，则调用 IAgentCheckpointService 保存现场后打断。
    /// </summary>
    bool SupportsInterruption { get; }
}
