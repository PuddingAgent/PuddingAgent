using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 优先级事件队列 — 三级持久队列（Urgent > Important > Normal）。
/// 使用 SQLite 持久化，进程重启不丢事件。
/// </summary>
public interface IPriorityEventQueue
{
    /// <summary>
    /// 入队。priority 决定进入哪个子队列。
    /// 返回持久化后的队列条目 ID。
    /// </summary>
    Task<string> EnqueueAsync(ProcessedEvent evt, EventPriorityLevel priority, CancellationToken ct = default);

    /// <summary>
    /// 出队。按优先级：先消费所有 Urgent → Important → Normal。
    /// 队列为空时返回 null。
    /// </summary>
    Task<QueuedEvent?> DequeueAsync(CancellationToken ct = default);

    /// <summary>
    /// 查看队列头部（不消费）。
    /// </summary>
    Task<QueuedEvent?> PeekAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取队列统计信息。
    /// </summary>
    Task<QueueStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// 更新队列条目状态（用于标记处理完成/失败）。
    /// </summary>
    Task UpdateStatusAsync(string eventId, string status, string? errorMessage = null, CancellationToken ct = default);
}
