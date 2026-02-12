using System.Text.RegularExpressions;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// Chat room message routing resolver.
/// <para>
/// Frontend, HTTP connectors, MQTT connectors, and future agent-to-agent tools
/// should be able to submit the same text shape. This resolver keeps @ routing
/// rules server-side so the browser remains only an observation and input window.
/// </para>
/// </summary>
public static partial class ChatRoomRouteResolver
{
    public const string AudienceAgent = "agent";
    public const string AudienceAll = "all";

    /// <summary>Resolve a user or connector chat message into backend routing intent.</summary>
    public static ChatRoomRoute Resolve(
        AdminChatRequest request,
        IReadOnlyList<WorkspaceAgentDto> workspaceAgents)
    {
        var originalText = string.IsNullOrWhiteSpace(request.OriginalMessageText)
            ? request.MessageText
            : request.OriginalMessageText!;
        var explicitTargets = NormalizeTargets(request.TargetAgentIds, workspaceAgents);
        var availableAgents = workspaceAgents
            .Where(agent => agent.IsEnabled && !agent.IsFrozen)
            .ToList();

        if (explicitTargets.Count > 0)
        {
            var explicitAudience = string.Equals(request.Audience, AudienceAll, StringComparison.OrdinalIgnoreCase)
                ? AudienceAll
                : AudienceAgent;
            return new ChatRoomRoute(
                MessageText: request.MessageText.Trim(),
                OriginalMessageText: originalText,
                Audience: explicitAudience,
                TargetAgentIds: explicitTargets,
                PrimaryAgentId: explicitTargets[0]);
        }

        var parsed = ParseLeadingMention(request.MessageText, availableAgents);
        if (parsed is not null)
        {
            return parsed;
        }

        var fallbackAgentId = ResolveFallbackAgentId(request.AgentId, availableAgents);
        return new ChatRoomRoute(
            MessageText: request.MessageText.Trim(),
            OriginalMessageText: originalText,
            Audience: AudienceAgent,
            TargetAgentIds: fallbackAgentId is null ? [] : [fallbackAgentId],
            PrimaryAgentId: fallbackAgentId);
    }

    private static ChatRoomRoute? ParseLeadingMention(
        string messageText,
        IReadOnlyList<WorkspaceAgentDto> availableAgents)
    {
        var trimmedStart = messageText.TrimStart();
        var leadingWhitespaceLength = messageText.Length - trimmedStart.Length;
        var match = LeadingMentionRegex().Match(trimmedStart);
        if (!match.Success) return null;

        var mention = match.Groups["mention"].Value;
        var routedText = messageText[(leadingWhitespaceLength + match.Length)..].TrimStart();
        if (mention.Equals(AudienceAll, StringComparison.OrdinalIgnoreCase))
        {
            var allTargets = availableAgents.Select(agent => agent.AgentId).ToList();
            return new ChatRoomRoute(
                MessageText: routedText,
                OriginalMessageText: messageText,
                Audience: AudienceAll,
                TargetAgentIds: allTargets,
                PrimaryAgentId: allTargets.FirstOrDefault());
        }

        var targetAgent = FindMentionedAgent(availableAgents, mention);
        if (targetAgent is null) return null;

        return new ChatRoomRoute(
            MessageText: routedText,
            OriginalMessageText: messageText,
            Audience: AudienceAgent,
            TargetAgentIds: [targetAgent.AgentId],
            PrimaryAgentId: targetAgent.AgentId);
    }

    private static List<string> NormalizeTargets(
        IReadOnlyList<string>? targetAgentIds,
        IReadOnlyList<WorkspaceAgentDto> workspaceAgents)
    {
        if (targetAgentIds is null || targetAgentIds.Count == 0) return [];

        var available = workspaceAgents
            .Where(agent => agent.IsEnabled && !agent.IsFrozen)
            .ToDictionary(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);
        return targetAgentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(id => available.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveFallbackAgentId(
        string? requestedAgentId,
        IReadOnlyList<WorkspaceAgentDto> availableAgents)
    {
        if (!string.IsNullOrWhiteSpace(requestedAgentId)
            && availableAgents.Any(agent => agent.AgentId.Equals(requestedAgentId, StringComparison.OrdinalIgnoreCase)))
        {
            return requestedAgentId;
        }

        return availableAgents.FirstOrDefault()?.AgentId ?? requestedAgentId;
    }

    private static WorkspaceAgentDto? FindMentionedAgent(
        IReadOnlyList<WorkspaceAgentDto> agents,
        string mention)
    {
        return agents.FirstOrDefault(agent =>
            IsMentionMatch(agent.AgentId, mention)
            || IsMentionMatch(agent.Name, mention)
            || IsMentionMatch(agent.DisplayName, mention));
    }

    private static bool IsMentionMatch(string? candidate, string mention) =>
        !string.IsNullOrWhiteSpace(candidate)
        && candidate.Trim().Equals(mention.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^@(?<mention>[^\\s@]+)(?:\\s+|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMentionRegex();
}

/// <summary>Resolved chat room routing intent.</summary>
public sealed record ChatRoomRoute(
    string MessageText,
    string OriginalMessageText,
    string Audience,
    IReadOnlyList<string> TargetAgentIds,
    string? PrimaryAgentId);
