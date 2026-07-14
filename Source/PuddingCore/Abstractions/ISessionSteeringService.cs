namespace PuddingCode.Abstractions;

/// <summary>
/// Manages session steering messages injected into the agent loop.
/// </summary>
public interface ISessionSteeringService
{
    Task<SteeringMessage?> CreateAsync(string workspaceId, string sessionId, string messageText, string? agentId = null, int priority = 100, CancellationToken ct = default);
    Task<IReadOnlyList<SteeringMessage>> GetPendingAsync(string sessionId, CancellationToken ct = default);
    Task MarkConsumedAsync(string sessionId, string steeringId, CancellationToken ct = default);
}

public sealed record SteeringMessage
{
    public string SteeringId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string MessageText { get; init; } = string.Empty;
    public int Priority { get; init; } = 100;
    public bool IsConsumed { get; init; }
    public long CreatedAt { get; init; }
    public string? ConsumedAt { get; init; }
}
