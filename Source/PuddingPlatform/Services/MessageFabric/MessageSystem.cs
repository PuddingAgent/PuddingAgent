using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingPlatform.Services.MessageFabric;

/// <summary>
/// High-level message fabric service.
/// <para>
/// It owns message-domain orchestration and delegates priority, retries, and
/// subscription wakeups to the internal event system.
/// </para>
/// </summary>
public sealed class MessageSystem : IMessageSystem
{
    private readonly IMessageRouter _router;
    private readonly MessageFabricStore _store;
    private readonly IInternalEventBus _eventBus;
    private readonly WorkspaceRoomParticipantProvider _participants;
    private readonly ILogger<MessageSystem> _logger;

    public MessageSystem(
        IMessageRouter router,
        MessageFabricStore store,
        IInternalEventBus eventBus,
        WorkspaceRoomParticipantProvider participants,
        ILogger<MessageSystem>? logger = null)
    {
        _router = router;
        _store = store;
        _eventBus = eventBus;
        _participants = participants;
        _logger = logger ?? NullLogger<MessageSystem>.Instance;
    }

    public async Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default)
    {
        var workspaceId = envelope.From.WorkspaceId ?? "default";
        var roomId = string.IsNullOrWhiteSpace(envelope.RoomId) ? "default" : envelope.RoomId!;
        var participants = await _participants.GetParticipantsAsync(workspaceId, roomId, ct: ct);
        var plan = await _router.RouteAsync(envelope with { RoomId = roomId }, participants, ct);

        _logger.LogInformation(
            "[MessageFabric] send workspace_id={workspace_id} room_id={room_id} message_id={message_id} delivery_id={delivery_id} target_kind={target_kind} target_id={target_id} status={status} attempt_count={attempt_count} execution_id={execution_id} correlation_id={correlation_id} causation_id={causation_id}",
            workspaceId,
            roomId,
            envelope.MessageId,
            string.Join(",", plan.Deliveries.Select(delivery => delivery.DeliveryId)),
            envelope.To.Count == 1 ? envelope.To[0].Kind : "multiple",
            envelope.To.Count == 1 ? envelope.To[0].Id : "multiple",
            MessageDeliveryStatuses.Queued,
            0,
            null,
            envelope.CorrelationId,
            envelope.CausationId);

        await _store.PersistRouteAsync(workspaceId, plan, ct);

        foreach (var delivery in plan.Deliveries)
        {
            await _eventBus.PublishAsync(new InternalEvent
            {
                Type = "message.deliver",
                WorkspaceId = workspaceId,
                AgentId = delivery.Target.Kind == MessageEndpointKinds.Agent ? delivery.Target.Id : null,
                SessionId = envelope.ConversationId,
                Priority = ResolvePriority(envelope.Priority),
                Source = new EventSource
                {
                    SourceType = "message",
                    SourceId = envelope.MessageId,
                },
                Payload = new MessageDeliverEventPayload
                {
                    MessageId = envelope.MessageId,
                    DeliveryId = delivery.DeliveryId,
                    WorkspaceId = workspaceId,
                    RoomId = roomId,
                    From = envelope.From,
                    Target = delivery.Target,
                    Content = envelope.Content,
                    ReplyToMessageId = envelope.ReplyToMessageId,
                    Metadata = envelope.Metadata,
                },
                CorrelationId = envelope.CorrelationId,
                CausationId = envelope.CausationId,
            }, ct);
        }

        return new MessageSendResult
        {
            MessageId = envelope.MessageId,
            RoomId = roomId,
            DeliveryIds = plan.Deliveries.Select(delivery => delivery.DeliveryId).ToList(),
        };
    }

    private static EventPriorityLevel ResolvePriority(int priority)
    {
        if (priority >= (int)EventPriorityLevel.Urgent)
            return EventPriorityLevel.Urgent;
        if (priority >= (int)EventPriorityLevel.Important)
            return EventPriorityLevel.Important;
        return EventPriorityLevel.Normal;
    }
}
