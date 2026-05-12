using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Events;

/// <summary>
/// 事件订阅工具 — Agent 可主动 subscribe / unsubscribe 事件类型。
/// 实现 IEventSubscriptionTool 和 IAgentSkill，同时作为 ITool 暴露给 LLM function calling。
/// </summary>
public class EventSubscriptionTool : IEventSubscriptionTool, IAgentSkill
{
    private readonly ILogger<EventSubscriptionTool> _logger;
    private readonly List<EventSubscription> _subscriptions = new(); // Phase 6 迁移到 SQLite
    private readonly object _lock = new();

    // ── IAgentSkill 成员 ──────────────────────────────────────────────
    public string SkillId => "event_subscribe";
    public string Name => "事件订阅管理";
    public string Description =>
        "管理Agent的事件订阅：subscribe_events（订阅事件类型，支持通配符如 mqtt.sensor.*）、" +
        "unsubscribe_events（取消订阅）、list_subscriptions（列出当前订阅）";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;

    public EventSubscriptionTool(ILogger<EventSubscriptionTool> logger)
    {
        _logger = logger;
    }

    public Task<EventSubscription> SubscribeAsync(
        string agentId,
        string workspaceId,
        string[] eventTypePatterns,
        string? filterExpression = null,
        CancellationToken ct = default)
    {
        var sub = new EventSubscription
        {
            AgentId = agentId,
            WorkspaceId = workspaceId,
            EventTypePattern = string.Join(",", eventTypePatterns),
            FilterExpression = filterExpression,
        };

        lock (_lock)
        {
            _subscriptions.Add(sub);
        }

        _logger.LogInformation("[EventSubscription] Agent={AgentId} subscribed: {Patterns}",
            agentId, sub.EventTypePattern);

        return Task.FromResult(sub);
    }

    public Task UnsubscribeAsync(string subscriptionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var sub = _subscriptions.FirstOrDefault(s => s.SubscriptionId == subscriptionId);
            if (sub is not null)
            {
                _subscriptions.Remove(sub);
                _logger.LogInformation("[EventSubscription] Unsubscribed: {Id}", subscriptionId);
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EventSubscription>> ListSubscriptionsAsync(
        string agentId,
        string workspaceId,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            var results = _subscriptions
                .Where(s => s.AgentId == agentId && s.WorkspaceId == workspaceId && s.Status == "active")
                .ToList();
            return Task.FromResult<IReadOnlyList<EventSubscription>>(results);
        }
    }

    public Task<IReadOnlyList<EventSubscription>> MatchSubscriptionsAsync(
        InternalEvent evt,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            var matched = _subscriptions
                .Where(s => s.Status == "active"
                    && MatchPattern(s.EventTypePattern, evt.Type)
                    && MatchFilter(s.FilterExpression, evt))
                .ToList();
            return Task.FromResult<IReadOnlyList<EventSubscription>>(matched);
        }
    }

    /// <summary>
    /// 简单通配符匹配：支持 * 和逗号分隔的多模式。
    /// 例："mqtt.sensor.*" 匹配 "mqtt.sensor.motion"
    /// </summary>
    private static bool MatchPattern(string pattern, string eventType)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var patterns = pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in patterns)
        {
            // 将通配符 * 转为正则 .*
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(eventType, regexPattern))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 简单过滤表达式解析：key=value 或 key>=value。
    /// </summary>
    private static bool MatchFilter(string? filterExpression, InternalEvent evt)
    {
        if (string.IsNullOrWhiteSpace(filterExpression)) return true;

        // 简单实现：解析 "priority>=5" 格式
        var parts = filterExpression.Split('=', 2);

        // TODO: Phase 6 完善过滤表达式解析
        return true;
    }

    // ── IAgentSkill.ExecuteAsync ──────────────────────────────────────

    /// <summary>
    /// Agent Skill 执行入口。解析 operation 参数分发到 subscribe/unsubscribe/list。
    /// </summary>
    public async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var operation = request.Parameters.TryGetValue("operation", out var op) ? op : "list";

        try
        {
            switch (operation)
            {
                case "subscribe":
                {
                    var patterns = request.Parameters.TryGetValue("event_type_patterns", out var p) && !string.IsNullOrWhiteSpace(p)
                        ? p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : [];
                    if (patterns.Length == 0)
                        return new SkillResult { Success = false, Output = "", Error = "event_type_patterns is required for subscribe", ExitCode = 1 };

                    var filter = request.Parameters.TryGetValue("filter_expression", out var f) ? f : null;
                    var sub = await SubscribeAsync(request.AgentInstanceId, request.WorkspaceId, patterns, filter, ct);
                    var result = JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        operation = "subscribe",
                        subscriptionId = sub.SubscriptionId,
                        patterns = sub.EventTypePattern,
                    });
                    return new SkillResult { Success = true, Output = result };
                }

                case "unsubscribe":
                {
                    var subId = request.Parameters.TryGetValue("subscription_id", out var sid) ? sid : null;
                    if (string.IsNullOrWhiteSpace(subId))
                        return new SkillResult { Success = false, Output = "", Error = "subscription_id is required for unsubscribe", ExitCode = 1 };

                    await UnsubscribeAsync(subId, ct);
                    var result = JsonSerializer.Serialize(new { status = "ok", operation = "unsubscribe", subscriptionId = subId });
                    return new SkillResult { Success = true, Output = result };
                }

                case "list":
                default:
                {
                    var subs = await ListSubscriptionsAsync(request.AgentInstanceId, request.WorkspaceId, ct);
                    var list = subs.Select(s => new
                    {
                        s.SubscriptionId,
                        s.EventTypePattern,
                        s.FilterExpression,
                        s.Status,
                        s.CreatedAt,
                    });
                    var result = JsonSerializer.Serialize(new { status = "ok", operation = "list", count = subs.Count, subscriptions = list });
                    return new SkillResult { Success = true, Output = result };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EventSubscription] ExecuteAsync failed operation={Operation}", operation);
            return new SkillResult { Success = false, Output = "", Error = ex.Message, ExitCode = 1 };
        }
    }
}
