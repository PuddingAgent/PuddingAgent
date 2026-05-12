using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 事件订阅工具 — Agent 可通过此工具订阅/取消订阅事件类型。
/// 作为 ITool 暴露给 LLM function calling。
/// </summary>
public interface IEventSubscriptionTool
{
    /// <summary>
    /// 订阅事件。eventTypePatterns 支持通配符，如 ["mqtt.sensor.*", "cron.*"]。
    /// filterExpression 可选，如 "priority>=5"。
    /// </summary>
    Task<EventSubscription> SubscribeAsync(
        string agentId,
        string workspaceId,
        string[] eventTypePatterns,
        string? filterExpression = null,
        CancellationToken ct = default);

    /// <summary>
    /// 取消订阅。
    /// </summary>
    Task UnsubscribeAsync(string subscriptionId, CancellationToken ct = default);

    /// <summary>
    /// 列出 Agent 当前所有活跃订阅。
    /// </summary>
    Task<IReadOnlyList<EventSubscription>> ListSubscriptionsAsync(
        string agentId,
        string workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// 检查给定事件是否匹配 Agent 的任一订阅。
    /// 返回匹配的订阅列表（代理模式：匹配则转发事件）。
    /// </summary>
    Task<IReadOnlyList<EventSubscription>> MatchSubscriptionsAsync(
        InternalEvent evt,
        CancellationToken ct = default);
}
