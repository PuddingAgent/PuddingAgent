using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// Passive Message Fabric admission. ChatMessage + message.created + head are
/// committed in one transaction; no command/Turn is created and no model wakes.
/// </summary>
public sealed class ConversationNotificationStore(
    PlatformDbContext db,
    ICommittedEventSignal committedSignal,
    ILogger<ConversationNotificationStore> logger)
    : IConversationNotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<ConversationNotificationResult> AcceptAsync(
        ConversationNotificationRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);

        var eventId = StableEventId(request.MessageId);
        var existingSequence = await db.ConversationEvents
            .AsNoTracking()
            .Where(evt => evt.ConversationId == request.ConversationId
                          && evt.EventId == eventId)
            .Select(evt => (long?)evt.Sequence)
            .SingleOrDefaultAsync(ct);
        var messageExists = await db.ChatMessages
            .AsNoTracking()
            .AnyAsync(message => message.MessageId == request.MessageId, ct);
        if (existingSequence.HasValue && messageExists)
        {
            return new ConversationNotificationResult(
                request.ConversationId,
                request.MessageId,
                existingSequence.Value,
                AlreadyAccepted: true);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!messageExists)
            {
                db.ChatMessages.Add(new ChatMessageEntity
                {
                    MessageId = request.MessageId,
                    SessionId = request.ConversationId,
                    WorkspaceId = request.WorkspaceId,
                    AgentInstanceId = request.AgentInstanceId,
                    Role = "user",
                    Content = request.Content,
                    UserId = request.UserId,
                    MetadataJson = SerializeMetadata(request.Metadata),
                    CreatedAt = request.CreatedAt,
                });
            }

            long acceptedSequence;
            if (existingSequence.HasValue)
            {
                acceptedSequence = existingSequence.Value;
            }
            else
            {
                var head = await db.ConversationHeads
                    .SingleOrDefaultAsync(
                        item => item.ConversationId == request.ConversationId,
                        ct);
                if (head is null)
                {
                    head = new ConversationHeadEntity
                    {
                        ConversationId = request.ConversationId,
                        HeadSequence = 0,
                    };
                    db.ConversationHeads.Add(head);
                }

                acceptedSequence = head.HeadSequence + 1;
                head.HeadSequence = acceptedSequence;
                var now = DateTimeOffset.UtcNow.ToString("O");
                var payload = JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = request.Content,
                    notification = true,
                    metadata = request.Metadata,
                }, JsonOptions);
                db.ConversationEvents.Add(new ConversationEventEntity
                {
                    ConversationId = request.ConversationId,
                    Sequence = acceptedSequence,
                    EventId = eventId,
                    WorkspaceId = request.WorkspaceId,
                    TurnId = string.Empty,
                    MessageId = request.MessageId,
                    Type = ConversationEventTypes.MessageCreated,
                    SchemaVersion = 1,
                    Payload = payload.GetRawText(),
                    OccurredAt = now,
                    CommittedAt = now,
                    CorrelationId = request.CorrelationId,
                    CausationId = request.CausationId,
                    AgentId = request.AgentInstanceId,
                    SourceKind = ConversationEventSourceKind.Agent
                        .ToString()
                        .ToLowerInvariant(),
                    ProducerComponent = "message.fabric.notification",
                });
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            committedSignal.Signal(request.ConversationId, acceptedSequence);
            logger.LogInformation(
                "[MessageFabric] Passive notification accepted conversation={ConversationId} message={MessageId} agent={AgentId} sequence={Sequence}",
                request.ConversationId,
                request.MessageId,
                request.AgentInstanceId,
                acceptedSequence);

            return new ConversationNotificationResult(
                request.ConversationId,
                request.MessageId,
                acceptedSequence,
                AlreadyAccepted: existingSequence.HasValue && messageExists);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static string StableEventId(string messageId)
        => $"notify:{messageId}";

    private static string? SerializeMetadata(
        IReadOnlyDictionary<string, string>? metadata)
        => metadata is null || metadata.Count == 0
            ? null
            : JsonSerializer.Serialize(metadata, JsonOptions);
}
