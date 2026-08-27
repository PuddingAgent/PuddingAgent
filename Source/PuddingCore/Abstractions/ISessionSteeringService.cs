namespace PuddingCode.Abstractions;

/// <summary>
/// Result of consuming a pending steering message for injection into the agent loop.
/// </summary>
public sealed record ConsumedSessionSteeringMessage(
    string SteeringId,
    string WorkspaceId,
    string SessionId,
    string TargetTurnId,
    string? AgentId,
    string MessageText,
    int Priority,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ConsumedAtUtc,
    int Round);

/// <summary>
/// Manages session steering messages injected into the agent loop.
/// </summary>
public interface ISessionSteeringService
{
    Task<ConsumedSessionSteeringMessage?> ConsumeNextAsync(
        string sessionId,
        string? agentId,
        string targetTurnId,
        int round,
        CancellationToken ct = default);
}
