using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.MessageFabric;

/// <summary>
/// SQLite-backed message fabric store.
/// <para>
/// Room messages are the transcript facts; message deliveries are the durable
/// per-endpoint facts used by both event delivery and pull-based inbox reads.
/// </para>
/// </summary>
public sealed class MessageFabricStore : IMessageInbox
{
    private readonly PlatformDbContext _db;
    private readonly ILogger<MessageFabricStore> _logger;

    public MessageFabricStore(PlatformDbContext db, ILogger<MessageFabricStore>? logger = null)
    {
        _db = db;
        _logger = logger ?? NullLogger<MessageFabricStore>.Instance;
    }

    public async Task PersistRouteAsync(
        string workspaceId,
        MessageRoutePlan plan,
        CancellationToken ct = default)
    {
        var exists = await _db.RoomMessages
            .AnyAsync(message => message.MessageId == plan.MessageId, ct);
        if (exists)
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _db.RoomMessages.Add(new RoomMessageEntity
        {
            MessageId = plan.RoomMessage.MessageId,
            WorkspaceId = workspaceId,
            RoomId = plan.RoomMessage.RoomId,
            FromKind = plan.RoomMessage.From.Kind,
            FromId = plan.RoomMessage.From.Id,
            FromDisplayName = plan.RoomMessage.From.DisplayName,
            Audience = plan.RoomMessage.Audience,
            Visibility = plan.RoomMessage.Visibility,
            Content = plan.RoomMessage.Content,
            CreatedAt = plan.RoomMessage.CreatedAt,
        });

        foreach (var delivery in plan.Deliveries)
        {
            _db.MessageDeliveries.Add(new MessageDeliveryEntity
            {
                DeliveryId = delivery.DeliveryId,
                MessageId = delivery.MessageId,
                WorkspaceId = workspaceId,
                RoomId = plan.RoomMessage.RoomId,
                TargetKind = delivery.Target.Kind,
                TargetId = delivery.Target.Id,
                TargetDisplayName = delivery.Target.DisplayName,
                Status = MessageDeliveryStatuses.Queued,
                Priority = delivery.Priority,
                AttemptCount = 0,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);
        foreach (var delivery in plan.Deliveries)
        {
            LogDeliveryTransition(
                workspaceId,
                plan.RoomMessage.RoomId,
                delivery.MessageId,
                delivery.DeliveryId,
                delivery.Target.Kind,
                delivery.Target.Id,
                MessageDeliveryStatuses.Queued,
                attemptCount: 0,
                executionId: null,
                correlationId: null,
                causationId: null);
        }
    }

    public async Task<IReadOnlyList<MessageInboxItem>> ListAsync(
        MessageInboxQuery query,
        CancellationToken ct = default)
    {
        var deliveries = _db.MessageDeliveries.AsNoTracking()
            .Where(delivery =>
                delivery.TargetKind == query.Endpoint.Kind
                && delivery.TargetId == query.Endpoint.Id);

        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
            deliveries = deliveries.Where(delivery => delivery.WorkspaceId == query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.RoomId))
            deliveries = deliveries.Where(delivery => delivery.RoomId == query.RoomId);

        if (!query.IncludeDelivered)
            deliveries = deliveries.Where(delivery => delivery.Status != MessageDeliveryStatuses.Delivered);

        var limited = await deliveries
            .OrderByDescending(delivery => delivery.Priority)
            .ThenBy(delivery => delivery.CreatedAt)
            .Take(Math.Max(1, query.Limit))
            .ToListAsync(ct);

        return await BuildInboxItemsAsync(limited, ct);
    }

    public async Task<IReadOnlyList<MessageDeliveryTarget>> ListPendingTargetsAsync(
        string targetKind,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetKind))
            throw new ArgumentException("Target kind is required.", nameof(targetKind));

        var targets = await _db.MessageDeliveries.AsNoTracking()
            .Where(delivery => delivery.TargetKind == targetKind)
            .Where(delivery =>
                delivery.Status == MessageDeliveryStatuses.Queued
                || delivery.Status == MessageDeliveryStatuses.Retrying)
            .Select(delivery => new
            {
                delivery.WorkspaceId,
                delivery.RoomId,
                delivery.TargetKind,
                delivery.TargetId,
            })
            .Distinct()
            .ToListAsync(ct);

        return targets
            .Select(target => new MessageDeliveryTarget
            {
                WorkspaceId = target.WorkspaceId,
                RoomId = target.RoomId,
                TargetKind = target.TargetKind,
                TargetId = target.TargetId,
            })
            .ToList();
    }

    public async Task<MessageInboxItem?> ClaimNextAsync(
        MessageClaimRequest request,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var leaseUntil = DateTimeOffset.UtcNow.Add(request.LeaseDuration).ToUnixTimeMilliseconds();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var query = _db.MessageDeliveries
            .Where(delivery =>
                delivery.TargetKind == request.Endpoint.Kind
                && delivery.TargetId == request.Endpoint.Id)
            .Where(delivery =>
                delivery.Status == MessageDeliveryStatuses.Queued
                || delivery.Status == MessageDeliveryStatuses.Retrying)
            .Where(delivery => delivery.AvailableAt == null || delivery.AvailableAt <= now);

        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
            query = query.Where(delivery => delivery.WorkspaceId == request.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(request.RoomId))
            query = query.Where(delivery => delivery.RoomId == request.RoomId);

        var delivery = await query
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (delivery is null)
            return null;

        delivery.Status = MessageDeliveryStatuses.Delivering;
        delivery.AttemptCount += 1;
        delivery.LeaseUntil = leaseUntil;
        delivery.ClaimedByExecutionId = request.ExecutionId;
        delivery.LastError = null;
        delivery.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        LogDeliveryTransition(delivery, request.ExecutionId);

        return (await BuildInboxItemsAsync([delivery], ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<MessageInboxItem>> ClaimBatchAsync(
        MessageClaimRequest request,
        int maxBatch,
        CancellationToken ct = default)
    {
        maxBatch = Math.Clamp(maxBatch, 1, 20);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var leaseUntil = DateTimeOffset.UtcNow.Add(request.LeaseDuration).ToUnixTimeMilliseconds();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var query = _db.MessageDeliveries
            .Where(delivery =>
                delivery.TargetKind == request.Endpoint.Kind
                && delivery.TargetId == request.Endpoint.Id)
            .Where(delivery =>
                delivery.Status == MessageDeliveryStatuses.Queued
                || delivery.Status == MessageDeliveryStatuses.Retrying)
            .Where(delivery => delivery.AvailableAt == null || delivery.AvailableAt <= now);

        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
            query = query.Where(delivery => delivery.WorkspaceId == request.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(request.RoomId))
            query = query.Where(delivery => delivery.RoomId == request.RoomId);

        var deliveries = await query
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .Take(maxBatch)
            .ToListAsync(ct);

        if (deliveries.Count == 0)
            return [];

        foreach (var delivery in deliveries)
        {
            delivery.Status = MessageDeliveryStatuses.Delivering;
            delivery.AttemptCount += 1;
            delivery.LeaseUntil = leaseUntil;
            delivery.ClaimedByExecutionId = request.ExecutionId;
            delivery.LastError = null;
            delivery.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var delivery in deliveries)
            LogDeliveryTransition(delivery, request.ExecutionId);

        return await BuildInboxItemsAsync(deliveries, ct);
    }

    public async Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var expired = await _db.MessageDeliveries
            .Where(delivery =>
                delivery.Status == MessageDeliveryStatuses.Delivering
                && delivery.LeaseUntil != null
                && delivery.LeaseUntil < nowMs)
            .ToListAsync(ct);

        if (expired.Count == 0)
            return 0;

        foreach (var delivery in expired)
        {
            delivery.Status = MessageDeliveryStatuses.Retrying;
            delivery.AvailableAt = nowMs;
            delivery.LeaseUntil = null;
            delivery.ClaimedByExecutionId = null;
            delivery.UpdatedAt = nowMs;
        }

        await _db.SaveChangesAsync(ct);
        foreach (var delivery in expired)
            LogDeliveryTransition(delivery, executionId: null);

        return expired.Count;
    }

    public async Task AckAsync(string deliveryId, CancellationToken ct = default)
        => await AckAsync(deliveryId, executionId: "", ct);

    public async Task AckAsync(string deliveryId, string executionId, CancellationToken ct = default)
    {
        var delivery = await _db.MessageDeliveries
            .FirstOrDefaultAsync(item => item.DeliveryId == deliveryId, ct);
        if (delivery is null)
            return;
        if (!MatchesExecution(delivery, executionId))
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        delivery.Status = MessageDeliveryStatuses.Delivered;
        delivery.AckAt = now;
        delivery.LeaseUntil = null;
        delivery.ClaimedByExecutionId = null;
        delivery.LastError = null;
        delivery.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        LogDeliveryTransition(delivery, executionId);
    }

    public async Task RetryAsync(
        string deliveryId,
        string executionId,
        string error,
        DateTimeOffset availableAt,
        CancellationToken ct = default)
    {
        var delivery = await _db.MessageDeliveries
            .FirstOrDefaultAsync(item => item.DeliveryId == deliveryId, ct);
        if (delivery is null)
            return;
        if (!MatchesExecution(delivery, executionId))
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        delivery.Status = MessageDeliveryStatuses.Retrying;
        delivery.AvailableAt = availableAt.ToUnixTimeMilliseconds();
        delivery.LeaseUntil = null;
        delivery.ClaimedByExecutionId = null;
        delivery.LastError = error;
        delivery.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        LogDeliveryTransition(delivery, executionId);
    }

    public async Task DeadLetterAsync(
        string deliveryId,
        string executionId,
        string error,
        CancellationToken ct = default)
    {
        var delivery = await _db.MessageDeliveries
            .FirstOrDefaultAsync(item => item.DeliveryId == deliveryId, ct);
        if (delivery is null)
            return;
        if (!MatchesExecution(delivery, executionId))
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        delivery.Status = MessageDeliveryStatuses.DeadLetter;
        delivery.LeaseUntil = null;
        delivery.ClaimedByExecutionId = null;
        delivery.LastError = error;
        delivery.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        LogDeliveryTransition(delivery, executionId);
    }

    private async Task<IReadOnlyList<MessageInboxItem>> BuildInboxItemsAsync(
        IReadOnlyList<MessageDeliveryEntity> deliveries,
        CancellationToken ct)
    {
        var messageIds = deliveries.Select(delivery => delivery.MessageId).Distinct().ToList();
        var messages = await _db.RoomMessages.AsNoTracking()
            .Where(message => messageIds.Contains(message.MessageId))
            .ToDictionaryAsync(message => message.MessageId, ct);

        return deliveries.Select(delivery =>
        {
            messages.TryGetValue(delivery.MessageId, out var message);
            return new MessageInboxItem
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
        }).ToList();
    }

    private static bool MatchesExecution(MessageDeliveryEntity delivery, string executionId)
        => string.IsNullOrWhiteSpace(executionId)
           || string.IsNullOrWhiteSpace(delivery.ClaimedByExecutionId)
           || string.Equals(delivery.ClaimedByExecutionId, executionId, StringComparison.Ordinal);

    private void LogDeliveryTransition(MessageDeliveryEntity delivery, string? executionId) =>
        LogDeliveryTransition(
            delivery.WorkspaceId,
            delivery.RoomId,
            delivery.MessageId,
            delivery.DeliveryId,
            delivery.TargetKind,
            delivery.TargetId,
            delivery.Status,
            delivery.AttemptCount,
            executionId,
            correlationId: null,
            causationId: null);

    private void LogDeliveryTransition(
        string workspaceId,
        string? roomId,
        string messageId,
        string deliveryId,
        string targetKind,
        string targetId,
        string status,
        int attemptCount,
        string? executionId,
        string? correlationId,
        string? causationId)
    {
        _logger.LogInformation(
            "[MessageFabric] delivery_transition workspace_id={workspace_id} room_id={room_id} message_id={message_id} delivery_id={delivery_id} target_kind={target_kind} target_id={target_id} status={status} attempt_count={attempt_count} execution_id={execution_id} correlation_id={correlation_id} causation_id={causation_id}",
            workspaceId,
            roomId,
            messageId,
            deliveryId,
            targetKind,
            targetId,
            status,
            attemptCount,
            executionId,
            correlationId,
            causationId);
    }
}
