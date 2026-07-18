using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Read-only adapter for execution commands.
/// All command mutations are owned by the acceptance, lease, control and journal stores.
/// </summary>
public sealed class ExecutionCommandReader(
    IDbContextFactory<PlatformDbContext> dbFactory) : IExecutionCommandReader
{
    public async Task<ExecutionCommandRecord?> GetAsync(
        string commandId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ChatExecutionCommands
            .AsNoTracking()
            .FirstOrDefaultAsync(command => command.CommandId == commandId, ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<ExecutionCommandRecord?> FindByTurnIdAsync(
        string conversationId,
        string turnId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ChatExecutionCommands
            .AsNoTracking()
            .Where(command =>
                command.SessionId == conversationId &&
                command.TurnId == turnId)
            .OrderByDescending(command => command.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : Map(entity);
    }

    private static ExecutionCommandRecord Map(ChatExecutionCommandEntity entity) => new()
    {
        CommandId = entity.CommandId,
        WorkspaceId = entity.WorkspaceId,
        ConversationId = entity.SessionId,
        AssistantMessageId = entity.MessageId,
        UserMessageId = entity.UserMessageId,
        TurnId = entity.TurnId,
        AgentInstanceId = entity.AgentInstanceId,
        UserId = entity.UserId,
        ChannelId = entity.ChannelId,
        Status = ParseStatus(entity.Status),
        RunId = entity.RunId,
    };

    private static CommandStatus ParseStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "pending" => CommandStatus.Pending,
            "leased" => CommandStatus.Leased,
            "running" => CommandStatus.Running,
            "cancel_requested" => CommandStatus.CancelRequested,
            "succeeded" => CommandStatus.Succeeded,
            "failed" => CommandStatus.Failed,
            "cancelled" => CommandStatus.Cancelled,
            "lease_lost" => CommandStatus.LeaseLost,
            _ => CommandStatus.Pending,
        };
}
