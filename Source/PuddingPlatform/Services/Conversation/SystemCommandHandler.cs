using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// System-command boundary. A handled command writes a user/system transcript pair,
/// but never creates an execution command, ConversationTurn, or Agent run.
/// </summary>
public sealed class SystemCommandHandler(
    PlatformDbContext db,
    IRuntimeControlService runtimeControl,
    ILogger<SystemCommandHandler> logger) : ISystemCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<SystemCommandResult> HandleAsync(
        SystemCommandRequest request,
        CancellationToken ct = default)
    {
        Validate(request);

        if (!SystemCommandParser.TryParse(request.CommandText, out var command) ||
            command.CommandKind != SystemCommandKind.Yolo ||
            command.Action != SystemCommandAction.Run)
        {
            throw new NotSupportedException(
                "This endpoint currently accepts only the exact /yolo system command.");
        }

        var existing = await db.ChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                message =>
                    message.SessionId == request.ConversationId &&
                    message.CommandId == request.ClientRequestId &&
                    message.MessageId == request.ResponseMessageId,
                ct);
        if (existing is not null)
        {
            runtimeControl.SetMode(
                RuntimeExecutionMode.Yolo,
                $"idempotent replay of /yolo; user={request.UserId}; conversation={request.ConversationId}");
            return BuildResult(request, existing.Content, runtimeControl.Mode);
        }

        var action = runtimeControl.SetMode(
            RuntimeExecutionMode.Yolo,
            $"user command /yolo; user={request.UserId}; conversation={request.ConversationId}");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sourceMetadata = JsonSerializer.Serialize(
            new
            {
                sourceType = "system_command",
                sourceId = "system",
                sourceName = "System",
            },
            JsonOptions);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!await db.ChatMessages.AnyAsync(
                    message => message.MessageId == request.ClientMessageId,
                    ct))
            {
                db.ChatMessages.Add(new ChatMessageEntity
                {
                    MessageId = request.ClientMessageId,
                    SessionId = request.ConversationId,
                    WorkspaceId = request.WorkspaceId,
                    AgentInstanceId = request.AgentId,
                    Role = "user",
                    Content = command.RawText,
                    TurnId = request.ClientRequestId,
                    CommandId = request.ClientRequestId,
                    UserId = request.UserId,
                    CreatedAt = now,
                });
            }

            if (!await db.ChatMessages.AnyAsync(
                    message => message.MessageId == request.ResponseMessageId,
                    ct))
            {
                db.ChatMessages.Add(new ChatMessageEntity
                {
                    MessageId = request.ResponseMessageId,
                    SessionId = request.ConversationId,
                    WorkspaceId = request.WorkspaceId,
                    AgentInstanceId = request.AgentId,
                    Role = "agent",
                    Content = action.Message,
                    TurnId = request.ClientRequestId,
                    CommandId = request.ClientRequestId,
                    MetadataJson = sourceMetadata,
                    CreatedAt = now + 1,
                });
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        logger.LogWarning(
            "[SystemCommand] handled command={Command} workspace={WorkspaceId} conversation={ConversationId} user={UserId} mode={Mode}",
            command.RawText,
            request.WorkspaceId,
            request.ConversationId,
            request.UserId,
            runtimeControl.Mode);

        return BuildResult(request, action.Message, runtimeControl.Mode);
    }

    private static SystemCommandResult BuildResult(
        SystemCommandRequest request,
        string message,
        RuntimeExecutionMode mode) =>
        new(
            request.ConversationId,
            request.ClientMessageId,
            request.ResponseMessageId,
            request.CommandText.Trim(),
            message,
            mode.ToString());

    private static void Validate(SystemCommandRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResponseMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CommandText);
    }
}
