using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-07A 公开事件 DTO：插件只看到已授权、已过滤的交付；owner/generation/grant/cursor 由宿主绑定，不进入公开请求。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginSubscriptionRequest(string WorkspaceId, ImmutableArray<string> Kinds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginSubscriptionDescriptor(string SubscriptionId, long Generation, DateTimeOffset CreatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginEventDelivery(string EventId, string Kind, int SchemaVersion, DateTimeOffset OccurredAt,
    string WorkspaceId, string PayloadRef, string PayloadHash, string? CausationId = null, string? CorrelationId = null,
    string? BusinessRef = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginEventReadRequest(string SubscriptionId, int MaxCount);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginEventPage(ImmutableArray<PluginEventDelivery> Deliveries, bool SnapshotRequired,
    long OldestAvailableSequence = 0);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginEventAck(string SubscriptionId, ImmutableArray<string> EventIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginSubscriptionRef(string SubscriptionId);

public interface IPluginEventsApi
{
    Task<PluginSubscriptionDescriptor> SubscribeAsync(PluginSubscriptionRequest request, CancellationToken ct = default);
    Task<PluginEventPage> ReadAsync(PluginEventReadRequest request, CancellationToken ct = default);
    Task AckAsync(PluginEventAck request, CancellationToken ct = default);
    Task UnsubscribeAsync(PluginSubscriptionRef request, CancellationToken ct = default);
}

public sealed class PluginEventsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
