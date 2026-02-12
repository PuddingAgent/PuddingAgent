using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingPlatform.Services.MessageFabric;

/// <summary>
/// Pure message router that converts a message envelope into one room transcript draft
/// plus per-target delivery drafts.
/// </summary>
public sealed class MessageRouter : IMessageRouter
{
    public Task<MessageRoutePlan> RouteAsync(
        MessageEnvelope envelope,
        IReadOnlyList<RoomParticipant> participants,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envelope.RoomId))
            throw new InvalidOperationException("RoomId is required for room message routing.");

        var targets = ResolveTargets(envelope, participants);
        var deliveries = targets.Select(target => new MessageDeliveryDraft
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            MessageId = envelope.MessageId,
            Target = target,
            Priority = envelope.Priority,
        }).ToList();

        return Task.FromResult(new MessageRoutePlan
        {
            MessageId = envelope.MessageId,
            RoomMessage = new RoomMessageDraft
            {
                RoomId = envelope.RoomId,
                MessageId = envelope.MessageId,
                From = envelope.From,
                Audience = envelope.Audience,
                Visibility = envelope.Visibility,
                Content = envelope.Content,
                CreatedAt = envelope.CreatedAt,
            },
            Deliveries = deliveries,
        });
    }

    private static IReadOnlyList<MessageAddress> ResolveTargets(
        MessageEnvelope envelope,
        IReadOnlyList<RoomParticipant> participants)
    {
        if (string.Equals(envelope.Audience, MessageAudiences.Broadcast, StringComparison.OrdinalIgnoreCase))
        {
            var targets = participants
                .Where(p => p.Kind == MessageEndpointKinds.Agent
                    && p.CanReceive
                    && !string.Equals(p.Status, "disabled", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p.EndpointId, envelope.From.Id, StringComparison.Ordinal))
                .Select(p => new MessageAddress
                {
                    Kind = p.Kind,
                    Id = p.EndpointId,
                    WorkspaceId = envelope.From.WorkspaceId,
                    DisplayName = p.DisplayName,
                })
                .ToList();

            if (targets.Count == 0)
                throw new InvalidOperationException("No other agents in this room can receive broadcast messages.");

            return targets;
        }

        return envelope.To
            .Where(target => target.Kind != MessageEndpointKinds.Room)
            .Select(target => ResolveDirectTarget(target, participants))
            .GroupBy(target => $"{target.Kind}:{target.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static MessageAddress ResolveDirectTarget(
        MessageAddress target,
        IReadOnlyList<RoomParticipant> participants)
    {
        if (!string.Equals(target.Kind, MessageEndpointKinds.Agent, StringComparison.OrdinalIgnoreCase))
            return target;

        var matches = participants
            .Where(participant => string.Equals(participant.Kind, MessageEndpointKinds.Agent, StringComparison.OrdinalIgnoreCase))
            .Where(participant => participant.CanReceive)
            .Where(participant => !string.Equals(participant.Status, "disabled", StringComparison.OrdinalIgnoreCase))
            .Where(participant => MatchesAgentAddress(participant, target.Id))
            .ToList();

        if (matches.Count == 1)
        {
            var match = matches[0];
            return target with
            {
                Id = match.EndpointId,
                DisplayName = target.DisplayName ?? match.DisplayName,
            };
        }

        if (matches.Count > 1)
            throw new InvalidOperationException($"Agent address '{target.Id}' is ambiguous in this workspace.");

        throw new InvalidOperationException($"Agent address '{target.Id}' was not found or cannot receive messages in this workspace.");
    }

    private static bool MatchesAgentAddress(RoomParticipant participant, string rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
            return false;

        var address = rawAddress.Trim();
        return string.Equals(participant.EndpointId, address, StringComparison.OrdinalIgnoreCase)
            || participant.EndpointId.EndsWith(address, StringComparison.OrdinalIgnoreCase)
            || string.Equals(participant.DisplayName, address, StringComparison.OrdinalIgnoreCase);
    }
}
