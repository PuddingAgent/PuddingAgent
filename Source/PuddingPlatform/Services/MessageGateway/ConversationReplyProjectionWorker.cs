using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.MessageGateway;

/// <summary>
/// Projects committed terminal replies to durable Connector deliveries.
/// A projection retry reuses the same MessageId, while connector retry remains
/// an independent MessageDelivery and never re-executes the Agent.
/// </summary>
public sealed class ConversationReplyProjectionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ConversationReplyProjectionWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var projected = await ProjectBatchAsync(stoppingToken);
                if (projected == 0)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[MessageGateway] Reply projection loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public async Task<int> ProjectBatchAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var messageSystem = scope.ServiceProvider.GetRequiredService<IMessageSystem>();

        var commands = await db.ChatExecutionCommands
            .Where(command =>
                command.Status == "succeeded"
                && command.TerminalSequence != null
                && command.ReplyProjectedAt == null
                && command.MetadataJson != null
                && command.MetadataJson.Contains(MessageGatewayMetadata.IsGatewayIngress))
            .OrderBy(command => command.CompletedAt)
            .Take(20)
            .ToListAsync(ct);

        var projected = 0;
        foreach (var command in commands)
        {
            var metadata = DeserializeMetadata(command.MetadataJson);
            if (!IsTrue(Get(
                    metadata,
                    MessageGatewayMetadata.IsGatewayIngress)))
            {
                command.ReplyProjectedAt =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                projected++;
                continue;
            }
            if (!string.Equals(
                    Get(metadata, MessageGatewayMetadata.ChannelType),
                    "feishu",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var connectorId = Get(metadata, MessageGatewayMetadata.ConnectorId);
            var externalConversationId = Get(
                metadata,
                MessageGatewayMetadata.ExternalConversationId);
            if (string.IsNullOrWhiteSpace(connectorId)
                || string.IsNullOrWhiteSpace(externalConversationId))
            {
                command.ReplyProjectedAt =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                projected++;
                continue;
            }

            // A running connector stream owns this reply until it either creates
            // the durable final-card delivery or explicitly fails. Only a failed
            // stream falls back to the ordinary terminal text reply below.
            var streamProjection = await db.ConnectorStreamProjections
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    projection => projection.CommandId == command.CommandId
                                  && projection.ConnectorId == connectorId,
                    ct);
            if (streamProjection is not null
                && streamProjection.Status != ConnectorStreamProjectionStatuses.Failed)
            {
                continue;
            }

            var terminalEvent = await db.ConversationEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    evt =>
                        evt.ConversationId == command.SessionId
                        && evt.Sequence == command.TerminalSequence,
                    ct);
            if (terminalEvent is null)
                continue;

            var reply = ReadReply(terminalEvent.Payload);
            if (string.IsNullOrWhiteSpace(reply))
            {
                command.ReplyProjectedAt =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                projected++;
                continue;
            }

            var externalMessageId = Get(
                metadata,
                MessageGatewayMetadata.ExternalMessageId);
            var replyMessageId = StableId(
                "gateway-reply",
                command.CommandId,
                connectorId,
                externalMessageId ?? externalConversationId);
            metadata[MessageGatewayMetadata.ReplyProjectedMessageId] =
                replyMessageId;
            metadata[MessageGatewayMetadata.IdempotencyKey] = replyMessageId;

            await messageSystem.SendAsync(
                new MessageEnvelope
                {
                    MessageId = replyMessageId,
                    From = new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Agent,
                        Id = command.AgentInstanceId,
                        WorkspaceId = command.WorkspaceId,
                    },
                    To =
                    [
                        new MessageAddress
                        {
                            Kind = MessageEndpointKinds.Connector,
                            Id = connectorId,
                            WorkspaceId = command.WorkspaceId,
                        },
                    ],
                    RoomId = command.SessionId,
                    ConversationId = command.SessionId,
                    ReplyToMessageId = externalMessageId,
                    CorrelationId = command.SessionId,
                    CausationId = command.TurnId,
                    Audience = MessageAudiences.Direct,
                    Visibility = MessageVisibilities.Private,
                    ContentType = MessageContentTypes.Text,
                    Content = reply,
                    Metadata = metadata,
                },
                ct);

            command.ReplyProjectedAt =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            projected++;
            logger.LogInformation(
                "[MessageGateway] Reply projected command={CommandId} connector={ConnectorId} message={MessageId}",
                command.CommandId,
                connectorId,
                replyMessageId);
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        return projected;
    }

    private static Dictionary<string, string> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                       json,
                       JsonOptions)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? ReadReply(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("reply", out var reply)
                   && reply.ValueKind == JsonValueKind.String
                ? reply.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Get(
        IReadOnlyDictionary<string, string> metadata,
        string key)
        => metadata.TryGetValue(key, out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "1", StringComparison.Ordinal);

    private static string StableId(params string[] parts)
    {
        var raw = string.Join('\n', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
    }
}
