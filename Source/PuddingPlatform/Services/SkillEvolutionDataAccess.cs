using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// EF Core implementation of ISkillEvolutionDataAccess.
/// </summary>
public sealed class SkillEvolutionDataAccess : ISkillEvolutionDataAccess
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    public SkillEvolutionDataAccess(IDbContextFactory<PlatformDbContext> dbFactory) =>
        _dbFactory = dbFactory;

    public async Task<IReadOnlyList<ExecutionCommandRow>> GetRecentSuccessfulCommandsAsync(
        string workspaceId, string agentInstanceId, int limit, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entities = await db.ChatExecutionCommands
            .AsNoTracking()
            .Where(command => command.WorkspaceId == workspaceId
                              && command.AgentInstanceId == agentInstanceId
                              && command.Status == "succeeded")
            .OrderByDescending(command => command.CompletedAt ?? command.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(e => new ExecutionCommandRow
        {
            CommandId = e.CommandId,
            UserMessageId = e.UserMessageId,
            WorkspaceId = e.WorkspaceId,
            AgentInstanceId = e.AgentInstanceId,
            SessionId = e.SessionId,
            TurnId = e.TurnId,
            CompletedAt = e.CompletedAt,
            CreatedAt = e.CreatedAt,
            MetadataJson = e.MetadataJson,
        }).ToList();
    }

    public async Task<IReadOnlyList<ConversationEventRow>> GetEventsByCommandIdsAsync(
        string[] commandIds, string[] eventTypes, CancellationToken ct)
    {
        if (commandIds is null || commandIds.Length == 0)
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entities = await db.ConversationEvents
            .AsNoTracking()
            .Where(evt => evt.CommandId != null
                          && commandIds.Contains(evt.CommandId)
                          && eventTypes.Contains(evt.Type))
            .OrderBy(evt => evt.Sequence)
            .ToListAsync(ct);

        return entities.Select(e => new ConversationEventRow
        {
            CommandId = e.CommandId ?? string.Empty,
            Type = e.Type,
            Payload = e.Payload,
            Sequence = e.Sequence,
        }).ToList();
    }

    public async Task<Dictionary<string, string>> GetMessageContentsByIdsAsync(
        string[] messageIds, CancellationToken ct)
    {
        if (messageIds is null || messageIds.Length == 0)
            return new Dictionary<string, string>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.ChatMessages
            .AsNoTracking()
            .Where(message => messageIds.Contains(message.MessageId))
            .ToDictionaryAsync(
                message => message.MessageId,
                message => message.Content,
                ct);
    }
}
