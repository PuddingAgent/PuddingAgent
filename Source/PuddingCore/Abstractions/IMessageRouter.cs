using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>Routes message envelopes into room transcript facts and per-endpoint deliveries.</summary>
public interface IMessageRouter
{
    Task<MessageRoutePlan> RouteAsync(
        MessageEnvelope envelope,
        IReadOnlyList<RoomParticipant> participants,
        CancellationToken ct = default);
}
