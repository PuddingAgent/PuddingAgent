using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Publishes framework lifecycle hooks into Pudding's internal event pipeline.
/// Implementations must stay lightweight and must not run long business logic.
/// </summary>
public interface IHookPublisher
{
    Task<string> PublishAsync<TPayload>(
        HookEventName name,
        TPayload payload,
        HookPublishOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>Canonical Hook v2 event name wrapper.</summary>
public readonly record struct HookEventName(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Options used when mapping Hook v2 events to InternalEvent.</summary>
public sealed record HookPublishOptions
{
    public EventPriorityLevel Priority { get; init; } = EventPriorityLevel.Normal;
    public string SourceType { get; init; } = "framework";
    public string? SourceId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? CausationId { get; init; }
    public string? SessionId { get; init; }
    public string WorkspaceId { get; init; } = "default";
    public string? AgentId { get; init; }
}
