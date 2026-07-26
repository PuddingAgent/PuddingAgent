using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingAgent.Services;

/// <summary>
/// Durable Message Fabric → Connector egress dispatcher.
/// Delivery retry is independent from Agent execution, so a Feishu outage never
/// causes the originating Conversation Turn to run twice.
/// </summary>
public sealed class ConnectorDeliveryDispatcher(
    IInternalEventBus eventBus,
    IServiceScopeFactory scopeFactory,
    ConnectorHost connectorHost,
    ILogger<ConnectorDeliveryDispatcher> logger) : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private const int MaxAttempts = 10;

    private IEventSubscriptionHandle? _subscription;
    private CancellationTokenSource? _recoveryCts;
    private Task? _recoveryTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await eventBus.SubscribeAsync(
            "message.deliver",
            OnMessageDeliverAsync,
            cancellationToken);
        _recoveryCts = new CancellationTokenSource();
        _recoveryTask = RunRecoveryLoopAsync(_recoveryCts.Token);
        logger.LogInformation(
            "[ConnectorDelivery] Subscribed subscription={SubscriptionId}",
            _subscription.SubscriptionId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        _recoveryCts?.Cancel();
        if (_recoveryTask is not null)
        {
            try
            {
                await _recoveryTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _recoveryCts?.Dispose();
        _recoveryCts = null;
        _recoveryTask = null;
    }

    private async Task OnMessageDeliverAsync(InternalEvent evt)
        => await HandleAsync(evt, CancellationToken.None);

    /// <summary>
    /// Handles one durable connector delivery event. Public for deterministic
    /// integration tests; hosted operation reaches the same method via the bus.
    /// </summary>
    public async Task HandleAsync(
        InternalEvent evt,
        CancellationToken ct = default)
    {
        if (!string.Equals(
                evt.Type,
                "message.deliver",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payload = ReadPayload(evt);
        if (payload is null
            || !string.Equals(
                payload.Target.Kind,
                MessageEndpointKinds.Connector,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await TryDispatchAsync(
            payload.WorkspaceId,
            payload.RoomId,
            payload.Target.Id,
            ct);
    }

    private async Task TryDispatchAsync(
        string workspaceId,
        string? roomId,
        string connectorId,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IMessageInbox>();
        var executionId = $"connector-{Guid.NewGuid():N}";
        var claimed = await inbox.ClaimNextAsync(
            new MessageClaimRequest
            {
                Endpoint = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Connector,
                    Id = connectorId,
                    WorkspaceId = workspaceId,
                },
                WorkspaceId = workspaceId,
                RoomId = roomId,
                ExecutionId = executionId,
                LeaseDuration = TimeSpan.FromMinutes(2),
            },
            ct);
        if (claimed is null)
            return;

        try
        {
            var target = Get(
                    claimed.Metadata,
                    MessageGatewayMetadata.ExternalConversationId)
                ?? throw new InvalidOperationException(
                    "Connector delivery is missing external conversation target.");
            var connectorMetadata = new Dictionary<string, string>(
                claimed.Metadata,
                StringComparer.Ordinal);
            CopyAs(
                connectorMetadata,
                MessageGatewayMetadata.ExternalMessageId,
                "message_id");
            CopyAs(
                connectorMetadata,
                MessageGatewayMetadata.ExternalConversationId,
                "chat_id");
            CopyAs(
                connectorMetadata,
                MessageGatewayMetadata.IdempotencyKey,
                "uuid");

            await connectorHost.SendAsync(
                connectorId,
                new ConnectorMessage
                {
                    Target = target,
                    Content = claimed.Content,
                    Metadata = connectorMetadata,
                },
                ct);

            if (Get(
                    connectorMetadata,
                    ConnectorStreamMetadata.ProjectionId) is { } projectionId)
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var projection = await db.ConnectorStreamProjections
                    .SingleOrDefaultAsync(
                        item => item.ProjectionId == projectionId,
                        ct);
                if (projection is not null)
                {
                    projection.Status = ConnectorStreamProjectionStatuses.Completed;
                    projection.AttemptCount = 0;
                    projection.AvailableAt = null;
                    projection.LastError = null;
                    projection.UpdatedAt =
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            await inbox.AckAsync(claimed.DeliveryId, executionId, ct);
            logger.LogInformation(
                "[ConnectorDelivery] Delivered connector={ConnectorId} delivery={DeliveryId} attempt={Attempt}",
                connectorId,
                claimed.DeliveryId,
                claimed.AttemptCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (claimed.AttemptCount >= MaxAttempts)
            {
                await inbox.DeadLetterAsync(
                    claimed.DeliveryId,
                    executionId,
                    ex.Message,
                    CancellationToken.None);
                logger.LogError(
                    ex,
                    "[ConnectorDelivery] Dead-lettered connector={ConnectorId} delivery={DeliveryId} attempts={Attempts}",
                    connectorId,
                    claimed.DeliveryId,
                    claimed.AttemptCount);
                return;
            }

            var delaySeconds = Math.Min(
                300,
                5 * (1 << Math.Min(6, Math.Max(0, claimed.AttemptCount - 1))));
            await inbox.RetryAsync(
                claimed.DeliveryId,
                executionId,
                ex.Message,
                DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
                CancellationToken.None);
            logger.LogWarning(
                ex,
                "[ConnectorDelivery] Retrying connector={ConnectorId} delivery={DeliveryId} attempt={Attempt} delaySeconds={DelaySeconds}",
                connectorId,
                claimed.DeliveryId,
                claimed.AttemptCount,
                delaySeconds);
        }
    }

    private async Task RunRecoveryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var inbox = scope.ServiceProvider.GetRequiredService<IMessageInbox>();
                var targets = await inbox.ListPendingTargetsAsync(
                    MessageEndpointKinds.Connector,
                    ct);
                foreach (var target in targets)
                {
                    await TryDispatchAsync(
                        target.WorkspaceId,
                        target.RoomId,
                        target.TargetId,
                        ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ConnectorDelivery] Recovery pass failed");
            }
        }
    }

    private static MessageDeliverEventPayload? ReadPayload(InternalEvent evt)
    {
        if (evt.Payload is MessageDeliverEventPayload payload)
            return payload;
        if (evt.Payload is JsonElement json
            && json.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<MessageDeliverEventPayload>(
                json.GetRawText(),
                JsonOptions);
        }

        return null;
    }

    private static string? Get(
        IReadOnlyDictionary<string, string> metadata,
        string key)
        => metadata.TryGetValue(key, out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static void CopyAs(
        IDictionary<string, string> metadata,
        string sourceKey,
        string targetKey)
    {
        if (metadata.TryGetValue(sourceKey, out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            metadata[targetKey] = value;
        }
    }
}
