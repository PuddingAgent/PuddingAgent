namespace PuddingCode.Models;

/// <summary>
/// Enforces the OpenAI-compatible tool-call message protocol:
/// every assistant tool-call batch is immediately followed by exactly one
/// matching tool result for every advertised call id.
/// </summary>
public static class LlmMessageSequenceNormalizer
{
    public static LlmMessageNormalizationResult Normalize(IEnumerable<ChatMessage> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var messages = source.ToList();
        var normalized = new List<ChatMessage>(messages.Count);
        var orphanToolMessages = 0;
        var incompleteToolRounds = 0;
        var downgradedAssistantMessages = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            if (message.Role == ChatRole.Tool)
            {
                orphanToolMessages++;
                continue;
            }

            if (message.Role != ChatRole.Assistant || message.ToolCalls is not { Count: > 0 })
            {
                normalized.Add(message);
                continue;
            }

            var advertisedIds = message.ToolCalls
                .Select(call => call.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            var expectedIds = advertisedIds.ToHashSet(StringComparer.Ordinal);
            var matchingResults = new List<ChatMessage>();
            var matchedIds = new HashSet<string>(StringComparer.Ordinal);
            var cursor = i + 1;

            while (cursor < messages.Count && messages[cursor].Role == ChatRole.Tool)
            {
                var toolMessage = messages[cursor];
                if (!string.IsNullOrWhiteSpace(toolMessage.ToolCallId)
                    && expectedIds.Contains(toolMessage.ToolCallId)
                    && matchedIds.Add(toolMessage.ToolCallId))
                {
                    matchingResults.Add(toolMessage);
                }
                else
                {
                    orphanToolMessages++;
                }

                cursor++;
            }

            var hasValidCallIds = advertisedIds.Count == message.ToolCalls.Count
                && expectedIds.Count == advertisedIds.Count;
            var isComplete = hasValidCallIds
                && expectedIds.Count > 0
                && expectedIds.SetEquals(matchedIds);

            if (isComplete)
            {
                normalized.Add(message);
                normalized.AddRange(matchingResults);
            }
            else
            {
                incompleteToolRounds++;
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    normalized.Add(message with
                    {
                        ToolCalls = null,
                        ToolCallId = null,
                        ToolName = null,
                    });
                    downgradedAssistantMessages++;
                }
            }

            i = cursor - 1;
        }

        return new LlmMessageNormalizationResult(
            normalized,
            orphanToolMessages,
            incompleteToolRounds,
            downgradedAssistantMessages);
    }
}

public sealed record LlmMessageNormalizationResult(
    IReadOnlyList<ChatMessage> Messages,
    int DroppedOrphanToolMessages,
    int RepairedIncompleteToolRounds,
    int DowngradedAssistantMessages)
{
    public bool Changed =>
        DroppedOrphanToolMessages > 0
        || RepairedIncompleteToolRounds > 0;
}
