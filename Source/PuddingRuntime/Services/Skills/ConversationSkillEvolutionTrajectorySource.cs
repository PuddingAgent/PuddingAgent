using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingRuntime.Services.Skills;

/// <summary>Reads verified tool chains from the canonical conversation event store.</summary>
public sealed class ConversationSkillEvolutionTrajectorySource(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<ConversationSkillEvolutionTrajectorySource> logger)
    : ISkillEvolutionTrajectorySource
{
    public async Task<IReadOnlyList<SkillEvolutionTrajectory>> GetRecentSuccessfulAsync(
        string workspaceId,
        string agentInstanceId,
        int limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("Workspace id is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            throw new ArgumentException("Agent instance id is required.", nameof(agentInstanceId));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var commands = await db.ChatExecutionCommands
            .AsNoTracking()
            .Where(command => command.WorkspaceId == workspaceId
                              && command.AgentInstanceId == agentInstanceId
                              && command.Status == "succeeded")
            .OrderByDescending(command => command.CompletedAt ?? command.CreatedAt)
            .Take(Math.Clamp(limit, 1, 20))
            .ToListAsync(ct);

        if (commands.Count == 0)
            return [];

        var commandIds = commands.Select(command => command.CommandId).ToArray();
        var userMessageIds = commands.Select(command => command.UserMessageId).ToArray();
        var events = await db.ConversationEvents
            .AsNoTracking()
            .Where(evt => evt.CommandId != null
                          && commandIds.Contains(evt.CommandId)
                          && (evt.Type == ConversationEventTypes.ToolCallRequested
                              || evt.Type == ConversationEventTypes.ToolCallCompleted
                              || evt.Type == ConversationEventTypes.ToolCallFailed))
            .OrderBy(evt => evt.Sequence)
            .ToListAsync(ct);
        var goals = await db.ChatMessages
            .AsNoTracking()
            .Where(message => userMessageIds.Contains(message.MessageId))
            .ToDictionaryAsync(message => message.MessageId, message => message.Content, ct);

        var eventsByCommand = events
            .Where(evt => evt.CommandId is not null)
            .GroupBy(evt => evt.CommandId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var trajectories = new List<SkillEvolutionTrajectory>();

        foreach (var command in commands)
        {
            if (!eventsByCommand.TryGetValue(command.CommandId, out var commandEvents))
                continue;

            var steps = TryBuildSuccessfulSteps(commandEvents);
            if (steps is null || steps.Count < 2)
                continue;

            trajectories.Add(new SkillEvolutionTrajectory
            {
                WorkspaceId = command.WorkspaceId,
                AgentInstanceId = command.AgentInstanceId,
                SessionId = command.SessionId,
                TurnId = command.TurnId,
                Goal = goals.GetValueOrDefault(command.UserMessageId) ?? string.Empty,
                Steps = steps,
            });
        }

        logger.LogInformation(
            "[SkillEvolution] Loaded {Count} verified trajectories workspace={WorkspaceId} agent={AgentInstanceId}",
            trajectories.Count,
            workspaceId,
            agentInstanceId);
        return trajectories;
    }

    private static IReadOnlyList<SkillEvolutionToolStep>? TryBuildSuccessfulSteps(
        IReadOnlyList<ConversationEventEntity> events)
    {
        var pending = new List<(string Name, string Arguments)>();
        var steps = new List<SkillEvolutionToolStep>();

        foreach (var evt in events)
        {
            JsonElement payload;
            try
            {
                using var document = JsonDocument.Parse(evt.Payload);
                payload = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }

            if (evt.Type == ConversationEventTypes.ToolCallFailed)
                return null;

            if (evt.Type == ConversationEventTypes.ToolCallRequested)
            {
                var name = ReadString(payload, "name");
                if (string.IsNullOrWhiteSpace(name))
                    return null;
                pending.Add((name, ReadString(payload, "arguments") ?? "{}"));
                continue;
            }

            var completedName = ReadString(payload, "name");
            var pendingIndex = pending.FindIndex(item =>
                string.Equals(item.Name, completedName, StringComparison.Ordinal));
            if (pendingIndex < 0 || !IsSuccessful(payload))
                return null;

            var call = pending[pendingIndex];
            pending.RemoveAt(pendingIndex);
            steps.Add(new SkillEvolutionToolStep
            {
                ToolName = call.Name,
                Arguments = call.Arguments,
                Output = ReadString(payload, "output"),
            });
        }

        return pending.Count == 0 ? steps : null;
    }

    private static bool IsSuccessful(JsonElement payload)
    {
        if (!payload.TryGetProperty("exitCode", out var exit)
            || exit.ValueKind != JsonValueKind.Number
            || !exit.TryGetInt32(out var exitCode)
            || exitCode != 0)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(ReadString(payload, "error"));
    }

    private static string? ReadString(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
