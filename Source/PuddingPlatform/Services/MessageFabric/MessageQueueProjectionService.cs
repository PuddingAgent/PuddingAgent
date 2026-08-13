using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingCode.Services;
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

        if (!query.IncludeSystem)
        {
            deliveriesQuery = deliveriesQuery.Where(delivery =>
                _db.RoomMessages.Any(message =>
                    message.WorkspaceId == delivery.WorkspaceId
                    && message.MessageId == delivery.MessageId
                    && message.Visibility != MessageVisibilities.System));
        }

        if (!query.IncludeTerminal)
            deliveriesQuery = deliveriesQuery.Where(delivery => ActiveStatuses.Contains(delivery.Status));

        // Fetch the full filtered candidate queue once, ordered by availableAt
        // ascending (null = immediately available sorts first, SQLite semantics)
        // so every returned item can carry its true queue position for the Phase 2
        // "让位" (give-way) action. The display order (priority desc, created asc)
        // is applied afterwards in memory.
        var candidates = await deliveriesQuery
            .OrderBy(delivery => delivery.AvailableAt)
            .ThenBy(delivery => delivery.CreatedAt)
            .ThenBy(delivery => delivery.DeliveryId)
            .ToListAsync(ct);

        var positionByDeliveryId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
            positionByDeliveryId[candidates[i].DeliveryId] = i;

        var deliveries = candidates
            .OrderByDescending(delivery => delivery.Priority)
            .ThenBy(delivery => delivery.CreatedAt)
            .Take(limit)
            .ToList();

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
            .Select(delivery => Map(delivery, messages, positionByDeliveryId))
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
        IReadOnlyDictionary<string, RoomMessageEntity> messages,
        IReadOnlyDictionary<string, int> positionByDeliveryId)
    {
        messages.TryGetValue(delivery.MessageId, out var message);
        var envelope = message is null
            ? null
            : AgentContextEnvelopeRenderer.TryParse(message.Content);

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
            Content = envelope?.Context.Text ?? message?.Content ?? string.Empty,
            Audience = message?.Audience,
            Visibility = message?.Visibility,
            MessageType = envelope?.MessageType,
            ContentType = envelope?.ContentType,
            Status = delivery.Status,
            Substate = ResolveSubstate(delivery),
            Priority = delivery.Priority,
            AttemptCount = delivery.AttemptCount,
            DeferCount = delivery.DeferCount,
            ExecutionState = delivery.ExecutionState,
            Position = positionByDeliveryId.TryGetValue(delivery.DeliveryId, out var position) ? position : -1,
            CreatedAt = delivery.CreatedAt,
            AvailableAt = delivery.AvailableAt,
            LeaseUntil = delivery.LeaseUntil,
            ReadAt = delivery.ReadAt,
            AckAt = delivery.AckAt,
            ClaimedByExecutionId = delivery.ClaimedByExecutionId,
            LastError = delivery.LastError,
        };
    }

    /// <summary>
    /// Phase 2 projection contract — locked semantics, do not change:
    /// <list type="bullet">
    /// <item>queued + deferCount == 0 → "fresh"（普通排队）</item>
    /// <item>queued + deferCount &gt; 0 → "waiting"（busy 挂起）</item>
    /// <item>retrying → "retrying"（真实失败退避）</item>
    /// <item>delivered / dead_letter / failed → identity（终态三子态）</item>
    /// </list>
    /// Statuses outside the locked mapping (delivering / cancelled / expired) fall
    /// back to the status itself so no information is lost.
    /// </summary>
    private static string ResolveSubstate(MessageDeliveryEntity delivery)
        => delivery.Status switch
        {
            MessageDeliveryStatuses.Queued => delivery.DeferCount > 0 ? "waiting" : "fresh",
            MessageDeliveryStatuses.Retrying => "retrying",
            MessageDeliveryStatuses.Delivered => "delivered",
            MessageDeliveryStatuses.DeadLetter => "dead_letter",
            MessageDeliveryStatuses.Failed => "failed",
            _ => delivery.Status,
        };
}

public sealed record MessageQueueProjectionQuery
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public string? RoomId { get; init; }
    public int Limit { get; init; } = 50;
    public bool IncludeTerminal { get; init; }
    public bool IncludeSystem { get; init; }
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
    public string? Audience { get; init; }
    public string? Visibility { get; init; }
    public string? MessageType { get; init; }
    public string? ContentType { get; init; }
    public required string Status { get; init; }
    /// <summary>
    /// Phase 2 projection substate（仅投影计算，不入库）:
    /// fresh / waiting / retrying / delivered / dead_letter / failed（+ 身份回退）。
    /// </summary>
    public required string Substate { get; init; }
    public int Priority { get; init; }
    public int AttemptCount { get; init; }
    /// <summary>Busy 挂起次数（持久化列 defer_count）。</summary>
    public int DeferCount { get; init; }
    /// <summary>从 lastError 解析的 execution_state（"Busy" 或 null）。</summary>
    public string? ExecutionState { get; init; }
    /// <summary>
    /// 队列内序号：按 availableAt 升序排序后本消息的下标（0-based），
    /// 支撑前端「让位」动作。
    /// </summary>
    public int Position { get; init; }
    public long CreatedAt { get; init; }
    public long? AvailableAt { get; init; }
    public long? LeaseUntil { get; init; }
    public long? ReadAt { get; init; }
    public long? AckAt { get; init; }
    public string? ClaimedByExecutionId { get; init; }
    public string? LastError { get; init; }
}
