using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.MessageGateway;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Admin-only diagnostics for the real Pudding → Message Fabric → Feishu voice path.
/// The caller selects a configured channel, never an arbitrary Feishu target.
/// </summary>
[Authorize(Roles = "admin")]
[ApiController]
[Route("api/workspaces/{workspaceId}/debug/feishu-voice")]
public sealed class FeishuVoiceDebugController(
    PlatformDbContext db,
    ChannelConfigurationFileService channels,
    IMessageSystem messageSystem,
    ILogger<FeishuVoiceDebugController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [HttpGet("channels/{channelId}/route")]
    public async Task<IActionResult> GetRoute(
        string workspaceId,
        string channelId,
        [FromQuery] string? sourceCommandId,
        CancellationToken ct)
    {
        var resolution = await ResolveRouteAsync(
            workspaceId,
            channelId,
            sourceCommandId,
            ct);
        if (resolution.Route is null)
            return StatusCode(resolution.StatusCode, new { message = resolution.Error });

        var route = resolution.Route;
        return Ok(new FeishuVoiceDebugRouteResponse
        {
            WorkspaceId = workspaceId,
            ChannelId = channelId,
            TtsVoice = resolution.Channel!.Feishu!.TtsVoice,
            SourceCommandId = route.Command.CommandId,
            ConversationId = route.Command.SessionId,
            ExternalMessageId = Mask(route.ExternalMessageId),
            ExternalConversationId = Mask(route.ExternalConversationId),
            RouteCreatedAt = route.Command.CreatedAt,
        });
    }

    [HttpPost("channels/{channelId}/send")]
    public async Task<IActionResult> Send(
        string workspaceId,
        string channelId,
        [FromBody] FeishuVoiceDebugSendRequest request,
        CancellationToken ct)
    {
        if (!request.ConfirmSend)
        {
            return BadRequest(new
            {
                message =
                    "confirmSend=true is required because this endpoint sends a real Feishu voice message.",
            });
        }

        var text = request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { message = "text is required." });
        if (text.Length > FeishuTtsProjection.MaxTextCharacters)
        {
            return BadRequest(new
            {
                message =
                    $"text must not exceed {FeishuTtsProjection.MaxTextCharacters} characters.",
            });
        }

        var requestId = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey.Trim();
        if (requestId.Length > 128)
            return BadRequest(new { message = "idempotencyKey must not exceed 128 characters." });

        var resolution = await ResolveRouteAsync(
            workspaceId,
            channelId,
            request.SourceCommandId,
            ct);
        if (resolution.Route is null)
            return StatusCode(resolution.StatusCode, new { message = resolution.Error });

        var route = resolution.Route;
        var metadata = new Dictionary<string, string>(
            route.Metadata,
            StringComparer.Ordinal)
        {
            [MessageGatewayMetadata.TtsRepliesEnabled] = "true",
            [MessageGatewayMetadata.TtsVoice] =
                resolution.Channel!.Feishu!.TtsVoice,
            ["gateway_debug_voice"] = "true",
            ["gateway_debug_request_id"] = requestId,
        };
        var messageId = await FeishuTtsProjection.QueueAsync(
            messageSystem,
            $"debug-feishu-voice:{workspaceId}:{channelId}:{requestId}",
            workspaceId,
            route.Command.AgentInstanceId,
            route.ConnectorId,
            route.Command.SessionId,
            route.Command.TurnId,
            route.ExternalMessageId,
            route.ExternalConversationId,
            text,
            metadata,
            ct);
        var deliveryIds = await db.MessageDeliveries
            .AsNoTracking()
            .Where(delivery =>
                delivery.WorkspaceId == workspaceId
                && delivery.MessageId == messageId
                && delivery.TargetKind == MessageEndpointKinds.Connector)
            .OrderBy(delivery => delivery.Id)
            .Select(delivery => delivery.DeliveryId)
            .ToListAsync(ct);

        logger.LogInformation(
            "[FeishuVoiceDebug] Queued workspace={WorkspaceId} channel={ChannelId} sourceCommand={CommandId} message={MessageId} deliveries={DeliveryIds}",
            workspaceId,
            channelId,
            route.Command.CommandId,
            messageId,
            string.Join(",", deliveryIds));

        return AcceptedAtAction(
            nameof(GetStatus),
            new { workspaceId, messageId },
            new FeishuVoiceDebugSendResponse
            {
                MessageId = messageId,
                DeliveryIds = deliveryIds,
                Status = MessageDeliveryStatuses.Queued,
                SourceCommandId = route.Command.CommandId,
                TtsVoice = resolution.Channel.Feishu.TtsVoice,
            });
    }

    [HttpGet("messages/{messageId}")]
    public async Task<IActionResult> GetStatus(
        string workspaceId,
        string messageId,
        CancellationToken ct)
    {
        if (!IsSafeId(messageId))
            return BadRequest(new { message = "messageId is invalid." });

        var message = await db.RoomMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.WorkspaceId == workspaceId
                    && item.MessageId == messageId,
                ct);
        var metadata = DeserializeMetadata(message?.MetadataJson);
        if (message is null
            || !IsTrue(Get(metadata, "gateway_debug_voice"))
            || !string.Equals(
                Get(metadata, ConnectorPayloadMetadata.Kind),
                ConnectorPayloadKinds.TtsAudio,
                StringComparison.Ordinal))
        {
            return NotFound(new { message = "Feishu voice debug message was not found." });
        }

        var deliveries = await db.MessageDeliveries
            .AsNoTracking()
            .Where(delivery =>
                delivery.WorkspaceId == workspaceId
                && delivery.MessageId == messageId
                && delivery.TargetKind == MessageEndpointKinds.Connector)
            .OrderBy(delivery => delivery.Id)
            .Select(delivery => new FeishuVoiceDebugDeliveryResponse
            {
                DeliveryId = delivery.DeliveryId,
                ConnectorId = delivery.TargetId,
                Status = delivery.Status,
                AttemptCount = delivery.AttemptCount,
                LastError = delivery.LastError,
                CreatedAt = delivery.CreatedAt,
                UpdatedAt = delivery.UpdatedAt,
                AckAt = delivery.AckAt,
            })
            .ToListAsync(ct);

        return Ok(new FeishuVoiceDebugStatusResponse
        {
            MessageId = messageId,
            Status = SummarizeStatus(deliveries),
            TtsVoice = Get(metadata, MessageGatewayMetadata.TtsVoice),
            RequestId = Get(metadata, "gateway_debug_request_id"),
            Deliveries = deliveries,
        });
    }

    private async Task<RouteResolution> ResolveRouteAsync(
        string workspaceId,
        string channelId,
        string? sourceCommandId,
        CancellationToken ct)
    {
        if (!IsSafeId(workspaceId) || !IsSafeId(channelId))
            return RouteResolution.Fail(400, "workspaceId or channelId is invalid.");

        var channel = await channels.GetChannelAsync(channelId, ct);
        if (channel is null
            || !string.Equals(
                channel.WorkspaceId,
                workspaceId,
                StringComparison.Ordinal))
        {
            return RouteResolution.Fail(
                404,
                $"Channel '{channelId}' was not found in workspace '{workspaceId}'.");
        }
        if (!channel.IsEnabled
            || !string.Equals(
                channel.ProviderId,
                ChannelProviderKinds.Feishu,
                StringComparison.OrdinalIgnoreCase)
            || channel.Feishu is null)
        {
            return RouteResolution.Fail(409, "The selected Feishu channel is not enabled.");
        }
        if (!channel.Feishu.TtsRepliesEnabled)
            return RouteResolution.Fail(409, "Voice replies are disabled for this channel.");

        List<ChatExecutionCommandEntity> candidates;
        if (!string.IsNullOrWhiteSpace(sourceCommandId))
        {
            if (!IsSafeId(sourceCommandId))
                return RouteResolution.Fail(400, "sourceCommandId is invalid.");

            candidates = await db.ChatExecutionCommands
                .AsNoTracking()
                .Where(command =>
                    command.WorkspaceId == workspaceId
                    && command.CommandId == sourceCommandId)
                .ToListAsync(ct);
        }
        else
        {
            candidates = await db.ChatExecutionCommands
                .AsNoTracking()
                .Where(command =>
                    command.WorkspaceId == workspaceId
                    && command.MetadataJson != null
                    && command.MetadataJson.Contains(channelId))
                .OrderByDescending(command => command.CreatedAt)
                .Take(100)
                .ToListAsync(ct);
        }

        foreach (var command in candidates.OrderByDescending(item => item.CreatedAt))
        {
            var metadata = DeserializeMetadata(command.MetadataJson);
            if (!IsTrue(Get(metadata, MessageGatewayMetadata.IsGatewayIngress))
                || !string.Equals(
                    Get(metadata, MessageGatewayMetadata.ChannelId),
                    channelId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    Get(metadata, MessageGatewayMetadata.ChannelType),
                    ChannelProviderKinds.Feishu,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Get(metadata, MessageGatewayMetadata.ConnectorId),
                    $"feishu:{channelId}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var externalConversationId = Get(
                metadata,
                MessageGatewayMetadata.ExternalConversationId);
            var externalMessageId = Get(
                metadata,
                MessageGatewayMetadata.ExternalMessageId);
            var connectorId = Get(metadata, MessageGatewayMetadata.ConnectorId);
            if (externalConversationId is null
                || externalMessageId is null
                || connectorId is null
                || string.IsNullOrWhiteSpace(command.AgentInstanceId)
                || string.IsNullOrWhiteSpace(command.SessionId)
                || string.IsNullOrWhiteSpace(command.TurnId))
            {
                continue;
            }

            return RouteResolution.Ok(
                channel,
                new FeishuVoiceDebugRoute(
                    command,
                    metadata,
                    connectorId,
                    externalConversationId,
                    externalMessageId));
        }

        return RouteResolution.Fail(
            409,
            "No trusted Feishu ingress route is available. Send one message to this bot first.");
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

    private static string SummarizeStatus(
        IReadOnlyList<FeishuVoiceDebugDeliveryResponse> deliveries)
    {
        if (deliveries.Count == 0)
            return "missing";
        if (deliveries.All(item =>
                item.Status == MessageDeliveryStatuses.Delivered))
        {
            return MessageDeliveryStatuses.Delivered;
        }
        if (deliveries.Any(item =>
                item.Status == MessageDeliveryStatuses.DeadLetter))
        {
            return MessageDeliveryStatuses.DeadLetter;
        }
        if (deliveries.Any(item =>
                item.Status == MessageDeliveryStatuses.Retrying))
        {
            return MessageDeliveryStatuses.Retrying;
        }
        if (deliveries.Any(item =>
                item.Status == MessageDeliveryStatuses.Delivering))
        {
            return MessageDeliveryStatuses.Delivering;
        }

        return deliveries[0].Status;
    }

    private static string Mask(string value)
        => value.Length <= 10
            ? value
            : $"{value[..5]}…{value[^4..]}";

    private static bool IsSafeId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 128
           && value.All(character =>
               char.IsLetterOrDigit(character)
               || character is '-' or '_' or '.' or ':');

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

    private sealed record FeishuVoiceDebugRoute(
        ChatExecutionCommandEntity Command,
        Dictionary<string, string> Metadata,
        string ConnectorId,
        string ExternalConversationId,
        string ExternalMessageId);

    private sealed record RouteResolution(
        int StatusCode,
        string? Error,
        ChannelInstanceManifest? Channel,
        FeishuVoiceDebugRoute? Route)
    {
        public static RouteResolution Fail(int statusCode, string error)
            => new(statusCode, error, null, null);

        public static RouteResolution Ok(
            ChannelInstanceManifest channel,
            FeishuVoiceDebugRoute route)
            => new(200, null, channel, route);
    }
}

public sealed record FeishuVoiceDebugSendRequest
{
    public string? Text { get; init; }
    public bool ConfirmSend { get; init; }
    public string? SourceCommandId { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed record FeishuVoiceDebugRouteResponse
{
    public required string WorkspaceId { get; init; }
    public required string ChannelId { get; init; }
    public required string TtsVoice { get; init; }
    public required string SourceCommandId { get; init; }
    public required string ConversationId { get; init; }
    public required string ExternalMessageId { get; init; }
    public required string ExternalConversationId { get; init; }
    public long RouteCreatedAt { get; init; }
}

public sealed record FeishuVoiceDebugSendResponse
{
    public required string MessageId { get; init; }
    public required IReadOnlyList<string> DeliveryIds { get; init; }
    public required string Status { get; init; }
    public required string SourceCommandId { get; init; }
    public required string TtsVoice { get; init; }
}

public sealed record FeishuVoiceDebugStatusResponse
{
    public required string MessageId { get; init; }
    public required string Status { get; init; }
    public string? TtsVoice { get; init; }
    public string? RequestId { get; init; }
    public required IReadOnlyList<FeishuVoiceDebugDeliveryResponse> Deliveries { get; init; }
}

public sealed record FeishuVoiceDebugDeliveryResponse
{
    public required string DeliveryId { get; init; }
    public required string ConnectorId { get; init; }
    public required string Status { get; init; }
    public int AttemptCount { get; init; }
    public string? LastError { get; init; }
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
    public long? AckAt { get; init; }
}
