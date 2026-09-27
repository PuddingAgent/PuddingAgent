namespace PuddingChat;

/// <summary>Presentation only: canonical order, contiguous text/thinking and tool identity.</summary>
public sealed record FlowBlock(string Key, string Kind, string Text, string Status,
    string? Name = null, string? Arguments = null, string? Output = null, int? ExitCode = null);

public static class TurnFlow
{
    public static FlowBlock[] Build(IEnumerable<ProcessItem> source, string fallbackText)
    {
        var blocks = new List<FlowBlock>();
        var tools = new Dictionary<string, int>(StringComparer.Ordinal);
        string? previousKind = null;
        foreach (var item in ChatSelection.Ordered(source))
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
                // Identically named parallel calls are distinct. Never guess a missing call ID.
                var key = string.IsNullOrEmpty(item.ToolCallId) ? item.Id : $"tool:{item.TurnId}:{item.ToolCallId}";
                if (tools.TryGetValue(key, out var index))
                {
                    var before = blocks[index];
                    blocks[index] = before with { Status = item.Status, Name = item.Name ?? before.Name,
                        Arguments = item.Arguments ?? before.Arguments, Output = item.Output ?? item.Message ?? item.Text,
                        ExitCode = item.ExitCode };
                }
                else
                {
                    tools[key] = blocks.Count;
                    blocks.Add(new(key, "tool", item.Text, item.Status, item.Name, item.Arguments,
                        item.Kind == "tool_result" ? item.Output ?? item.Message ?? item.Text : null, item.ExitCode));
                }
            }
            else blocks.Add(new(item.Id, item.Kind, item.Text, item.Status, item.Name, item.Arguments, item.Output, item.ExitCode));
            // A tool result also separates reasoning/text segments, even though it updates an earlier row.
            previousKind = item.Kind;
        }
        if (!blocks.Any(b => b.Kind == "text") && !string.IsNullOrEmpty(fallbackText))
            blocks.Add(new("answer", "text", fallbackText, "done"));
        return blocks.ToArray();
    }
}

/// <summary>Cancelable in-process commit notification. Read the authoritative snapshot after wake-up.</summary>
public interface IConversationChanges
{
    Task WaitForChangeAsync(RoleKey role, string sessionId, long cursor, CancellationToken ct);
}
