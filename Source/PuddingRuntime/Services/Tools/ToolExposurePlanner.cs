using System.Text.Json;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Provider-independent tool exposure planning. It only changes which standard top-level
/// function definitions are sent on a round; provider-specific message-level tool declarations
/// deliberately do not belong here.
/// </summary>
internal static class ToolExposurePlanner
{
    internal const string SearchToolId = "search_tools";
    internal const int DeferredLoadingThreshold = 24;

    private static readonly HashSet<string> CoreToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        SearchToolId,
        "goal_read",
        "goal_update",
        "send_message",
        "receive_messages",
        "agent_status",
        "agent_diagnostics",
        "spawn_sub_agent",
        "query_sub_agents",
        "sleep",
    };

    internal static ToolExposurePlan CreatePlan(
        IReadOnlyList<LlmToolDefinition> availableTools,
        IReadOnlySet<string>? loadedToolIds = null,
        int activationThreshold = DeferredLoadingThreshold)
    {
        ArgumentNullException.ThrowIfNull(availableTools);

        var stableTools = availableTools
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (stableTools.Count <= Math.Max(1, activationThreshold)
            || stableTools.All(tool => !tool.Name.Equals(SearchToolId, StringComparison.OrdinalIgnoreCase)))
        {
            return new ToolExposurePlan(stableTools, stableTools.Count, DeferredLoadingEnabled: false);
        }

        IReadOnlySet<string> loaded = loadedToolIds
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visible = stableTools
            .Where(tool => CoreToolIds.Contains(tool.Name) || loaded.Contains(tool.Name))
            .ToList();

        // search_tools is the recovery path. If it disappears because of a registration or
        // capability mismatch, fail open to the already-authorized full set instead of making
        // deferred tools permanently unreachable.
        if (visible.All(tool => !tool.Name.Equals(SearchToolId, StringComparison.OrdinalIgnoreCase)))
            return new ToolExposurePlan(stableTools, stableTools.Count, DeferredLoadingEnabled: false);

        return new ToolExposurePlan(visible, stableTools.Count, DeferredLoadingEnabled: true);
    }

    internal static int RegisterSearchResult(
        string toolName,
        bool success,
        string? output,
        ISet<string> loadedToolIds,
        IReadOnlyList<LlmToolDefinition> availableTools)
    {
        ArgumentNullException.ThrowIfNull(loadedToolIds);
        ArgumentNullException.ThrowIfNull(availableTools);

        if (!success
            || !toolName.Equals(SearchToolId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("loaded_tool_ids", out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var availableIds = availableTools
                .Select(tool => tool.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var item in ids.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var toolId = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(toolId)
                    && availableIds.Contains(toolId)
                    && !toolId.Equals(SearchToolId, StringComparison.OrdinalIgnoreCase)
                    && loadedToolIds.Add(toolId))
                {
                    added++;
                }
            }

            return added;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}

internal sealed record ToolExposurePlan(
    IReadOnlyList<LlmToolDefinition> VisibleTools,
    int AvailableToolCount,
    bool DeferredLoadingEnabled)
{
    internal int DeferredToolCount => Math.Max(0, AvailableToolCount - VisibleTools.Count);
}
