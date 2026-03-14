using System.Text;
using PuddingCode.Models;

namespace PuddingCode.Core;

public sealed record ContextBudgetOptions(
    int MaxPromptTokens = 24000,
    int MaxHistoryMessages = 80,
    int PreserveTailMessages = 8);

internal static class ContextBudgetManager
{
    public static int TrimInPlace(List<ChatMessage> history, ContextBudgetOptions options)
    {
        if (history.Count <= 1)
            return 0;

        var removed = 0;
        var keepTail = Math.Max(2, options.PreserveTailMessages);

        while (NeedsTrim(history, options, keepTail))
        {
            var removeIndex = 1;
            var latestProtectedIndex = Math.Max(1, history.Count - keepTail);
            if (removeIndex >= latestProtectedIndex)
                break;

            history.RemoveAt(removeIndex);
            removed++;
        }

        return removed;
    }

    private static bool NeedsTrim(IReadOnlyList<ChatMessage> history, ContextBudgetOptions options, int keepTail)
    {
        if (history.Count > options.MaxHistoryMessages)
            return true;

        var tokens = EstimatePromptTokens(history);
        if (tokens <= options.MaxPromptTokens)
            return false;

        return history.Count > keepTail;
    }

    private static int EstimatePromptTokens(IReadOnlyList<ChatMessage> history)
    {
        var chars = 0;
        foreach (var m in history)
        {
            chars += m.Content?.Length ?? 0;
            chars += m.ReasoningContent?.Length ?? 0;
            chars += m.ToolCallId?.Length ?? 0;
            if (m.ToolCalls is not null)
            {
                foreach (var tc in m.ToolCalls)
                {
                    chars += tc.Id.Length + tc.Name.Length + tc.ArgumentsJson.Length;
                }
            }
        }

        return Math.Max(1, chars / 4);
    }
}

