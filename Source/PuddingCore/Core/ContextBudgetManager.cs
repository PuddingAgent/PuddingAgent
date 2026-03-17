using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Core;

public sealed record ContextBudgetOptions(
    int MaxPromptTokens = 24000,
    int MaxHistoryMessages = 80,
    int PreserveTailMessages = 8,
    /// <summary>
    /// Number of older messages to compress into one summary per compression pass.
    /// Only used when an <see cref="IHistoryCompressor"/> is supplied.
    /// </summary>
    int CompressionWindowSize = 16);

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

    /// <summary>
    /// Attempts layered compression when budget is exceeded.
    /// Summarises the oldest <see cref="ContextBudgetOptions.CompressionWindowSize"/> messages
    /// using <paramref name="compressor"/>, replacing them with a single context-summary message.
    /// Falls back to <see cref="TrimInPlace"/> when compression is unavailable or returns null.
    /// </summary>
    public static async Task<int> CompressAndTrimAsync(
        List<ChatMessage> history,
        ContextBudgetOptions options,
        IHistoryCompressor compressor,
        CancellationToken ct = default)
    {
        if (history.Count <= 1)
            return 0;

        var keepTail = Math.Max(2, options.PreserveTailMessages);

        if (!NeedsTrim(history, options, keepTail))
            return 0;

        // Compressible zone: after system prompt (index 0), before the preserved tail.
        var compressEnd = Math.Max(1, history.Count - keepTail);
        if (compressEnd <= 1)
            return TrimInPlace(history, options);

        var windowSize = Math.Clamp(options.CompressionWindowSize, 2, 32);
        var windowEnd = Math.Min(compressEnd, 1 + windowSize);
        var window = history.GetRange(1, windowEnd - 1);

        if (window.Count < 2)
            return TrimInPlace(history, options);

        try
        {
            var summary = await compressor.CompressAsync(window, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                history.RemoveRange(1, window.Count);
                history.Insert(1, new ChatMessage(
                    ChatRole.System,
                    $"[Context summary — {window.Count} earlier messages]\n{summary}"));
                return window.Count - 1; // net reduction
            }
        }
        catch
        {
            // Compression failed; fall through to regular trim.
        }

        return TrimInPlace(history, options);
    }

    public static int EstimateTokens(IReadOnlyList<ChatMessage> history) =>
        EstimatePromptTokens(history);

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

