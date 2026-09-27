namespace PuddingChat;

/// <summary>Presentation only: canonical order, contiguous text/thinking and explicit activity identity.</summary>
public sealed record FlowBlock(string Key, string Kind, string Text, string Status,
    string? Name = null, string? Arguments = null, string? Output = null, int? ExitCode = null,
    string? ParentKey = null, int Depth = 0, string? DelegationExecutionId = null);

public static class TurnFlow
{
    private static string ToolKey(ProcessItem item) => string.IsNullOrEmpty(item.ToolCallId)
        ? item.Id : $"tool:{item.TurnId}:{item.ToolCallId}";
    private static string DelegationKey(ProcessItem item) => !string.IsNullOrEmpty(item.DelegationExecutionId)
        ? $"delegation:{item.TurnId}:run:{item.DelegationExecutionId}"
        // Missing execution identity cannot prove that two uses of a pooled Agent are the same run.
        : item.Id;

    public static FlowBlock[] Build(IEnumerable<ProcessItem> source, string fallbackText)
    {
        var ordered = ChatSelection.Ordered(source);
        var toolGroups = ordered.Where(i => i.Kind is "tool_call" or "tool_result").GroupBy(ToolKey)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var delegationGroups = ordered.Where(i => i.Kind == "delegation").GroupBy(DelegationKey)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var blocks = new List<FlowBlock>();
        string? previousKind = null;
        foreach (var item in ordered)
        {
            if (item.Kind is "text" or "thinking")
            {
                if (string.IsNullOrEmpty(item.Text)) continue;
                if (previousKind == item.Kind && blocks.Count > 0)
                    blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + item.Text };
                else blocks.Add(new(item.Id, item.Kind, item.Text, item.Status));
            }
            else if (item.Kind is "tool_call" or "tool_result")
            {
                var key = ToolKey(item); var events = toolGroups[key];
                var call = events.FirstOrDefault(e => e.Kind == "tool_call");
                var result = events.LastOrDefault(e => e.Kind == "tool_result");
                var anchor = call ?? events[0];
                if (item.Id == anchor.Id)
                {
                    var parent = call?.ParentToolCallId ?? result?.ParentToolCallId;
                    blocks.Add(new(key, "tool", anchor.Text,
                        result?.Status is "human_decision_required" or "dependency_wait" ? result.Status :
                        result?.ExitCode is not null and not 0 ? "error" : result?.Status ?? anchor.Status,
                        call?.Name ?? result?.Name, call?.Arguments ?? result?.Arguments,
                        result?.Output ?? result?.Message ?? result?.Text, result?.ExitCode,
                        parent is null ? null : $"tool:{anchor.TurnId}:{parent}"));
                }
            }
            else if (item.Kind == "delegation")
            {
                var key = DelegationKey(item); var events = delegationGroups[key];
                var first = events[0]; var latest = events[^1];
                if (item.Id == first.Id)
                    blocks.Add(new(key, "delegation", first.Text, latest.Status, first.Name ?? latest.Name,
                        Output: first.Id == latest.Id ? latest.Output : latest.Output ?? latest.Text,
                        DelegationExecutionId: first.DelegationExecutionId));
            }
            else blocks.Add(new(item.Id, item.Kind, item.Text, item.Status, item.Name, item.Arguments, item.Output, item.ExitCode));
            previousKind = item.Kind;
        }
        if (!blocks.Any(b => b.Kind == "text") && !string.IsNullOrEmpty(fallbackText))
            blocks.Add(new("answer", "text", fallbackText, "done"));
        return NestTools(blocks);
    }

    private static FlowBlock[] NestTools(List<FlowBlock> blocks)
    {
        var tools = blocks.Where(b => b.Kind == "tool").ToDictionary(b => b.Key, StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools.Values)
        {
            if (tool.ParentKey is null || !tools.ContainsKey(tool.ParentKey)) continue;
            var seen = new HashSet<string> { tool.Key };
            var parent = tool.ParentKey; var cycle = false;
            while (tools.TryGetValue(parent, out var candidate))
            {
                if (!seen.Add(parent)) { cycle = true; break; }
                if (candidate.ParentKey is null) break;
                parent = candidate.ParentKey;
            }
            if (!cycle) parents[tool.Key] = tool.ParentKey;
        }
        var children = blocks.Where(b => parents.ContainsKey(b.Key)).GroupBy(b => parents[b.Key])
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var result = new List<FlowBlock>();
        // Iterative traversal: malformed or deeply nested external events cannot recurse the UI stack.
        foreach (var root in blocks.Where(b => !parents.ContainsKey(b.Key)))
        {
            var pending = new Stack<(FlowBlock Block, int Depth)>(); pending.Push((root, 0));
            while (pending.TryPop(out var next))
            {
                result.Add(next.Block with { Depth = next.Depth });
                if (children.TryGetValue(next.Block.Key, out var nested))
                    for (var i = nested.Length - 1; i >= 0; i--) pending.Push((nested[i], next.Depth + 1));
            }
        }
        return result.ToArray();
    }

    public static string StatusLabel(string status) => status.ToLowerInvariant() switch
    {
        "running" or "started" or "created" => "运行中",
        "success" or "done" or "completed" => "已完成",
        "error" or "failed" => "失败",
        "cancelled" or "canceled" => "已取消",
        "budget_exhausted" => "预算耗尽",
        "timed_out" or "timeout" => "超时",
        "interrupted" => "已中断",
        "human_decision_required" => "需人工决定",
        "dependency_wait" => "等待依赖恢复",
        _ => status
    };
}

/// <summary>Cancelable in-process commit notification. Read the authoritative snapshot after wake-up.</summary>
public interface IConversationChanges
{
    Task WaitForChangeAsync(RoleKey role, string sessionId, long cursor, CancellationToken ct);
}
