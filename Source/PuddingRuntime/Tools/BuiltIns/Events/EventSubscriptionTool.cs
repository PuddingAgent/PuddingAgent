using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Events;

/// <summary>
/// 事件订阅工具 — Agent 可主动 subscribe / unsubscribe 事件类型。
/// </summary>
[Tool(
    id: "event_subscribe",
    name: "事件订阅管理",
    description: "管理Agent的事件订阅：subscribe_events（订阅事件类型，支持通配符如 mqtt.sensor.*）、unsubscribe_events（取消订阅）、list_subscriptions（列出当前订阅）",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Medium)]
public class EventSubscriptionTool : PuddingToolBase<EventSubscriptionArgs>, IEventSubscriptionTool
{
    private readonly ILogger<EventSubscriptionTool> _logger;
    private readonly List<EventSubscription> _subscriptions = new();
    private readonly object _lock = new();

    // ── IEventSubscriptionTool （系统级，不受 Agent 调用影响）──

    public Task<EventSubscription> SubscribeAsync(string agentId, string workspaceId, string[] eventTypePatterns, string? filterExpression = null, CancellationToken ct = default)
    {
        var sub = new EventSubscription { AgentId = agentId, WorkspaceId = workspaceId, EventTypePattern = string.Join(",", eventTypePatterns), FilterExpression = filterExpression };
        lock (_lock) { _subscriptions.Add(sub); }
        _logger.LogInformation("[EventSubscription] Agent={AgentId} subscribed: {Patterns}", agentId, sub.EventTypePattern);
        return Task.FromResult(sub);
    }

    public Task UnsubscribeAsync(string subscriptionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var sub = _subscriptions.FirstOrDefault(s => s.SubscriptionId == subscriptionId);
            if (sub is not null) { _subscriptions.Remove(sub); _logger.LogInformation("[EventSubscription] Unsubscribed: {Id}", subscriptionId); }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EventSubscription>> ListSubscriptionsAsync(string agentId, string workspaceId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var results = _subscriptions.Where(s => s.AgentId == agentId && s.WorkspaceId == workspaceId && s.Status == "active").ToList();
            return Task.FromResult<IReadOnlyList<EventSubscription>>(results);
        }
    }

    public Task<IReadOnlyList<EventSubscription>> MatchSubscriptionsAsync(InternalEvent evt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var matched = _subscriptions.Where(s => s.Status == "active" && MatchPattern(s.EventTypePattern, evt.Type) && MatchFilter(s.FilterExpression, evt)).ToList();
            return Task.FromResult<IReadOnlyList<EventSubscription>>(matched);
        }
    }

    private static bool MatchPattern(string pattern, string eventType)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        foreach (var p in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(eventType, regexPattern)) return true;
        }
        return false;
    }

    private static bool MatchFilter(string? filterExpression, InternalEvent evt)
    {
        if (string.IsNullOrWhiteSpace(filterExpression)) return true;
        var geIdx = filterExpression.IndexOf(">=", StringComparison.Ordinal);
        if (geIdx >= 0)
        {
            var key = filterExpression[..geIdx].Trim(); var val = filterExpression[(geIdx + 2)..].Trim();
            var propVal = GetEventProperty(evt, key); if (propVal == null) return false;
            return int.TryParse(val, out var num) && int.TryParse(propVal, out var propNum) ? propNum >= num : string.Compare(propVal, val, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        var eqIdx = filterExpression.IndexOf('=', StringComparison.Ordinal);
        if (eqIdx >= 0)
        {
            var key = filterExpression[..eqIdx].Trim(); var val = filterExpression[(eqIdx + 1)..].Trim();
            var propVal = GetEventProperty(evt, key);
            return propVal != null && propVal.Equals(val, StringComparison.OrdinalIgnoreCase);
        }
        return evt.Type.Contains(filterExpression, StringComparison.OrdinalIgnoreCase) || (evt.Payload != null && evt.Payload.ToString()!.Contains(filterExpression, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetEventProperty(InternalEvent evt, string key) => key.ToLowerInvariant() switch
    {
        "type" => evt.Type, "priority" => evt.Priority.ToString(), _ => null
    };

    // ── Core execution ──

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        EventSubscriptionArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var result = await ExecuteCore(args.Operation ?? "list", args.EventTypePatterns ?? args.EventTypes,
            args.SubscriptionId, args.FilterExpression, context.AgentInstanceId, context.WorkspaceId, ct);
        return new ToolExecutionResult { Success = result.Success, Output = result.Output, Error = result.Error, ExitCode = result.ExitCode };
    }

    private async Task<SkillResult> ExecuteCore(string operation, string? patterns, string? subId, string? filter,
        string agentId, string workspaceId, CancellationToken ct)
    {
        try
        {
            switch (operation)
            {
                case "subscribe":
                {
                    var pts = !string.IsNullOrWhiteSpace(patterns) ? patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
                    if (pts.Length == 0) return Fail("event_type_patterns is required for subscribe");
                    var sub = await SubscribeAsync(agentId, workspaceId, pts, filter, ct);
                    return Ok(JsonSerializer.Serialize(new { status = "ok", operation = "subscribe", subscriptionId = sub.SubscriptionId, patterns = sub.EventTypePattern }));
                }
                case "unsubscribe":
                {
                    if (string.IsNullOrWhiteSpace(subId)) return Fail("subscription_id is required for unsubscribe");
                    await UnsubscribeAsync(subId, ct);
                    return Ok(JsonSerializer.Serialize(new { status = "ok", operation = "unsubscribe", subscriptionId = subId }));
                }
                case "list":
                default:
                {
                    var subs = await ListSubscriptionsAsync(agentId, workspaceId, ct);
                    var list = subs.Select(s => new { s.SubscriptionId, s.EventTypePattern, s.FilterExpression, s.Status, s.CreatedAt });
                    return Ok(JsonSerializer.Serialize(new { status = "ok", operation = "list", count = subs.Count, subscriptions = list }));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EventSubscription] Failed operation={Operation}", operation);
            return Fail(ex.Message);
        }
    }

    private static SkillResult Ok(string o) => new() { Success = true, Output = o, ExitCode = 0 };
    private static SkillResult Fail(string e) => new() { Success = false, Output = "", Error = e, ExitCode = 1 };
}

public sealed record EventSubscriptionArgs
{
    [ToolParam("Operation: subscribe / unsubscribe / list.")]
    public string? Operation { get; init; }
    [ToolParam("Comma-separated event type patterns, supports wildcards such as mqtt.sensor.*.")]
    public string? EventTypePatterns { get; init; }
    [ToolParam("@Deprecated — use event_type_patterns instead")]
    public string? EventTypes { get; init; }
    [ToolParam("Subscription id. Required for unsubscribe.")]
    public string? SubscriptionId { get; init; }
    [ToolParam("Optional filter expression, such as priority>=5.")]
    public string? FilterExpression { get; init; }
}
