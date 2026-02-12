using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.MessageFabric;

public sealed class MessageQueueProjectionService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private static readonly string[] ActiveStatuses =
    [
        MessageDeliveryStatuses.Queued,
        MessageDeliveryStatuses.Delivering,
        MessageDeliveryStatuses.Retrying,
    ];

    private readonly PlatformDbContext _db;

    public MessageQueueProjectionService(PlatformDbContext db)
    {
        _db = db;
    }

    public async Task<MessageQueueSnapshot> GetAgentQueueAsync(
        MessageQueueProjectionQuery query,
        CancellationToken ct = default)
    {
        var limit = query.Limit is > 0 and <= MaxLimit
            ? query.Limit
            : DefaultLimit;

        var deliveriesQuery = _db.MessageDeliveries.AsNoTracking()
            .Where(delivery =>
                delivery.WorkspaceId == query.WorkspaceId &&
                delivery.TargetKind == MessageEndpointKinds.Agent &&
                delivery.TargetId == query.AgentId);

        if (!string.IsNullOrWhiteSpace(query.RoomId))
            deliveriesQuery = deliveriesQuery.Where(delivery => delivery.RoomId == query.RoomId);

        if (!query.IncludeTerminal)
            deliveriesQuery = deliveriesQuery.Where(delivery => ActiveStatuses.Contains(delivery.Status));

        var deliveries = await deliveriesQuery
            .OrderByDescending(delivery => delivery.Priority)
            .ThenBy(delivery => delivery.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        var messageIds = deliveries
            .Select(delivery => delivery.MessageId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var messageRows = messageIds.Count == 0
            ? new List<RoomMessageEntity>()
            : await _db.RoomMessages.AsNoTracking()
                .Where(message => message.WorkspaceId == query.WorkspaceId && messageIds.Contains(message.MessageId))
                .OrderByDescending(message => message.CreatedAt)
                .ToListAsync(ct);
        var messages = messageRows
            .GroupBy(message => message.MessageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var items = deliveries
            .Select(delivery => Map(delivery, messages))
            .ToList();

        return new MessageQueueSnapshot
        {
            WorkspaceId = query.WorkspaceId,
            AgentId = query.AgentId,
            RoomId = query.RoomId,
            Items = items,
        };
    }

    private static MessageQueueItem Map(
        MessageDeliveryEntity delivery,
        IReadOnlyDictionary<string, RoomMessageEntity> messages)
    {
        messages.TryGetValue(delivery.MessageId, out var message);

        return new MessageQueueItem
        {
            DeliveryId = delivery.DeliveryId,
            MessageId = delivery.MessageId,
            WorkspaceId = delivery.WorkspaceId,
            RoomId = delivery.RoomId,
            From = new MessageAddress
            {
                Kind = message?.FromKind ?? MessageEndpointKinds.System,
                Id = message?.FromId ?? "unknown",
                WorkspaceId = delivery.WorkspaceId,
                DisplayName = message?.FromDisplayName,
            },
            Target = new MessageAddress
            {
                Kind = delivery.TargetKind,
                Id = delivery.TargetId,
                WorkspaceId = delivery.WorkspaceId,
                DisplayName = delivery.TargetDisplayName,
            },
            Content = message?.Content ?? string.Empty,
            Status = delivery.Status,
            Priority = delivery.Priority,
            AttemptCount = delivery.AttemptCount,
            CreatedAt = delivery.CreatedAt,
            AvailableAt = delivery.AvailableAt,
            LeaseUntil = delivery.LeaseUntil,
            ReadAt = delivery.ReadAt,
            AckAt = delivery.AckAt,
            ClaimedByExecutionId = delivery.ClaimedByExecutionId,
            LastError = delivery.LastError,
        };
    }
}

public sealed record MessageQueueProjectionQuery
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public string? RoomId { get; init; }
    public int Limit { get; init; } = 50;
    public bool IncludeTerminal { get; init; }
}

public sealed record MessageQueueSnapshot
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public string? RoomId { get; init; }
    public required IReadOnlyList<MessageQueueItem> Items { get; init; }
}

public sealed record MessageQueueItem
{
    public required string DeliveryId { get; init; }
    public required string MessageId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? RoomId { get; init; }
    public required MessageAddress From { get; init; }
    public required MessageAddress Target { get; init; }
    public required string Content { get; init; }
    public required string Status { get; init; }
    public int Priority { get; init; }
    public int AttemptCount { get; init; }
    public long CreatedAt { get; init; }
    public long? AvailableAt { get; init; }
    public long? LeaseUntil { get; init; }
    public long? ReadAt { get; init; }
    public long? AckAt { get; init; }
    public string? ClaimedByExecutionId { get; init; }
    public string? LastError { get; init; }
}
