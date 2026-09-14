using System.Text.Json;
using PuddingCode.Models;

namespace PuddingRuntime.Services;

/// <summary>
/// Reconciles canonical transcript content against an already normalized warm model history.
/// A mismatch never edits either list: the caller retains its normal hydration policy.
/// </summary>
internal static class HistoryPrefixReconciler
{
    internal static string ComputeSourceHash(string? content, IReadOnlyList<LlmContentPart>? parts)
        => CompositionSnapshot.Sha256Hex($"{content ?? string.Empty}\n{EncodeParts(parts)}");

    private static string EncodeParts(IReadOnlyList<LlmContentPart>? parts)
        => parts is { Count: > 0 } ? ContentPartsEnvelope.Encode(parts) : string.Empty;

    internal static bool TryAppendCanonicalTail(
        List<ChatMessage> history,
        IReadOnlyList<ChatMessage> canonical,
        out int appended)
    {
        appended = 0;
        if (history.Count == 0 || canonical.Count == 0)
            return false;

        var index = history[0].Role == ChatRole.System ? 1 : 0;
        if (index == history.Count)
            return false;

        var matched = 0;
        var pendingToolEvidence = false;
        while (index < history.Count)
        {
            var current = history[index++];
            if (matched < canonical.Count && Equivalent(current, canonical[matched]))
            {
                matched++;
                pendingToolEvidence = false;
                continue;
            }

            // Canonical ChatMessages can omit native tool protocol messages. Keep the
            // complete live round, but require a following matching transcript message:
            // never infer that an unprojected terminal result has already been observed.
            if (current.Role == ChatRole.Tool || current.ToolCalls is { Count: > 0 })
            {
                pendingToolEvidence = true;
                continue;
            }

            return false;
        }

        if (matched == 0 || pendingToolEvidence)
            return false;

        // Mutation is delayed until the entire warm prefix has been verified.
        for (var i = matched; i < canonical.Count; i++)
            history.Add(canonical[i]);
        appended = canonical.Count - matched;
        return true;
    }

    private static bool Equivalent(ChatMessage live, ChatMessage persisted)
    {
        if (live.Role != persisted.Role)
            return false;

        if (live.Role == ChatRole.User
            && !string.IsNullOrEmpty(live.SourceContentHash)
            && !string.IsNullOrEmpty(persisted.SourceContentHash))
        {
            return string.Equals(live.SourceContentHash, persisted.SourceContentHash, StringComparison.Ordinal);
        }

        // Reasoning/continuation are model-only state absent from the text projection;
        // retain them when content and protocol/visual/audio fields are unchanged.
        return string.Equals(live.Content, persisted.Content, StringComparison.Ordinal)
            && string.Equals(live.ToolCallId, persisted.ToolCallId, StringComparison.Ordinal)
            && string.Equals(live.ToolName, persisted.ToolName, StringComparison.Ordinal)
            && JsonSerializer.Serialize(live.ToolCalls) == JsonSerializer.Serialize(persisted.ToolCalls)
            && EncodeParts(live.ContentParts) == EncodeParts(persisted.ContentParts)
            && JsonSerializer.Serialize(live.VisualArtifactIds) == JsonSerializer.Serialize(persisted.VisualArtifactIds)
            && JsonSerializer.Serialize(live.AudioArtifactIds) == JsonSerializer.Serialize(persisted.AudioArtifactIds);
    }
}
