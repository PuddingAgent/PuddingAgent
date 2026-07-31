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
/// Admin-only diagnostic for the real generate → Vision Artifact → Message
/// Fabric → Feishu image path. The caller selects a configured channel, never
/// an arbitrary external target.
/// </summary>
[Authorize(Roles = "admin")]
[ApiController]
[Route("api/workspaces/{workspaceId}/debug/feishu-image")]
public sealed class FeishuImageDebugController(
    PlatformDbContext db,
    ChannelConfigurationFileService channels,
    IImageGenerationService imageGeneration,
    IMessageSystem messageSystem,
    ILogger<FeishuImageDebugController> logger)
    : ControllerBase
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
            return StatusCode(
                resolution.StatusCode,
                new { message = resolution.Error });

        return Ok(new
        {
            workspaceId,
            channelId,
            sourceCommandId = resolution.Route.Command.CommandId,
            conversationId = resolution.Route.Command.SessionId,
            routeCreatedAt = resolution.Route.Command.CreatedAt,
        });
    }

    [HttpPost("channels/{channelId}/generate-and-send")]
    public async Task<IActionResult> GenerateAndSend(
        string workspaceId,
        string channelId,
        [FromBody] FeishuImageDebugRequest request,
        CancellationToken ct)
    {
        if (!request.ConfirmSend)
        {
            return BadRequest(new
            {
                message =
                    "confirmSend=true is required because this endpoint generates a billable image and sends a real Feishu message.",
            });
        }
        var prompt = request.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            return BadRequest(new { message = "prompt is required." });

        var resolution = await ResolveRouteAsync(
            workspaceId,
            channelId,
            request.SourceCommandId,
            ct);
        if (resolution.Route is null)
            return StatusCode(
                resolution.StatusCode,
                new { message = resolution.Error });

        var generated = await imageGeneration.GenerateAsync(
            new ImageGenerationRequest
            {
                WorkspaceId = workspaceId,
                Prompt = prompt,
                ProviderId = request.ProviderId,
                ModelId = request.ModelId,
                Mode = string.IsNullOrWhiteSpace(request.Mode)
                    ? "default"
                    : request.Mode.Trim(),
                Size = string.IsNullOrWhiteSpace(request.Size)
                    ? "2K"
                    : request.Size.Trim(),
                Watermark = request.Watermark ?? true,
                OutputFormat = string.IsNullOrWhiteSpace(request.OutputFormat)
                    ? "png"
                    : request.OutputFormat.Trim(),
                OptimizePromptMode =
                    string.IsNullOrWhiteSpace(request.OptimizePromptMode)
                        ? "standard"
                        : request.OptimizePromptMode.Trim(),
                EnableWebSearch = request.EnableWebSearch ?? false,
                ImageCount = request.ImageCount ?? 1,
                ReferenceArtifactIds =
                    request.ReferenceArtifactIds?
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item.Trim())
                        .ToList()
                    ?? [],
            },
            ct);
        var route = resolution.Route;
        var requestId = Guid.NewGuid().ToString("N");
        var messageIds = new List<string>(generated.Artifacts.Count);
        foreach (var artifact in generated.Artifacts)
        {
            var metadata = new Dictionary<string, string>(
                route.Metadata,
                StringComparer.Ordinal)
            {
                ["gateway_debug_image"] = "true",
                ["gateway_debug_request_id"] = requestId,
                ["gateway_debug_image_artifact_id"] = artifact.ArtifactId,
            };
            messageIds.Add(await FeishuImageProjection.QueueAsync(
                messageSystem,
                $"debug-feishu-image:{workspaceId}:{channelId}:{requestId}",
                workspaceId,
                route.Command.AgentInstanceId,
                route.ConnectorId,
                route.Command.SessionId,
                route.Command.TurnId,
                route.ExternalMessageId,
                route.ExternalConversationId,
                artifact.ArtifactId,
                metadata,
                ct));
        }
        var primaryMessageId = messageIds[0];
        var deliveryIds = await db.MessageDeliveries
            .AsNoTracking()
            .Where(delivery =>
                delivery.WorkspaceId == workspaceId
                && messageIds.Contains(delivery.MessageId)
                && delivery.TargetKind == MessageEndpointKinds.Connector)
            .OrderBy(delivery => delivery.Id)
            .Select(delivery => delivery.DeliveryId)
            .ToListAsync(ct);

        logger.LogInformation(
            "[FeishuImageDebug] Queued workspace={WorkspaceId} channel={ChannelId} artifacts={ArtifactCount} messages={MessageCount}",
            workspaceId,
            channelId,
            generated.Artifacts.Count,
            messageIds.Count);
        return AcceptedAtAction(
            nameof(GetStatus),
            new { workspaceId, messageId = primaryMessageId },
            new
            {
                generated.ArtifactId,
                artifactIds = generated.Artifacts
                    .Select(item => item.ArtifactId)
                    .ToList(),
                generated.Artifacts,
                generated.MimeType,
                generated.ProviderId,
                generated.ModelId,
                messageId = primaryMessageId,
                messageIds,
                deliveryIds,
                status = MessageDeliveryStatuses.Queued,
                sourceCommandId = route.Command.CommandId,
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
            || !IsTrue(Get(metadata, "gateway_debug_image"))
            || !string.Equals(
                Get(metadata, ConnectorPayloadMetadata.Kind),
                ConnectorPayloadKinds.VisionImage,
                StringComparison.Ordinal))
        {
            return NotFound(new
            {
                message = "Feishu image debug message was not found.",
            });
        }

        var deliveries = await db.MessageDeliveries
            .AsNoTracking()
            .Where(delivery =>
                delivery.WorkspaceId == workspaceId
                && delivery.MessageId == messageId
                && delivery.TargetKind == MessageEndpointKinds.Connector)
            .OrderBy(delivery => delivery.Id)
            .Select(delivery => new
            {
                delivery.DeliveryId,
                connectorId = delivery.TargetId,
                delivery.Status,
                delivery.AttemptCount,
                delivery.LastError,
                delivery.CreatedAt,
                delivery.UpdatedAt,
                delivery.AckAt,
            })
            .ToListAsync(ct);
        return Ok(new
        {
            messageId,
            artifactId = Get(
                metadata,
                ConnectorPayloadMetadata.ArtifactId),
            requestId = Get(metadata, "gateway_debug_request_id"),
            deliveries,
        });
    }

    private async Task<RouteResolution> ResolveRouteAsync(
        string workspaceId,
        string channelId,
        string? sourceCommandId,
        CancellationToken ct)
    {
        if (!IsSafeId(workspaceId) || !IsSafeId(channelId))
            return RouteResolution.Fail(
                400,
                "workspaceId or channelId is invalid.");

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
            return RouteResolution.Fail(
                409,
                "The selected Feishu channel is not enabled.");
        }

        List<ChatExecutionCommandEntity> candidates;
        if (!string.IsNullOrWhiteSpace(sourceCommandId))
        {
            if (!IsSafeId(sourceCommandId))
                return RouteResolution.Fail(
                    400,
                    "sourceCommandId is invalid.");
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

        foreach (var command in candidates.OrderByDescending(
                     item => item.CreatedAt))
        {
            var metadata = DeserializeMetadata(command.MetadataJson);
            if (!IsTrue(Get(
                    metadata,
                    MessageGatewayMetadata.IsGatewayIngress))
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
            var connectorId = Get(
                metadata,
                MessageGatewayMetadata.ConnectorId);
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
                new FeishuImageDebugRoute(
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

    private static bool IsSafeId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 128
           && value.All(character =>
               char.IsLetterOrDigit(character)
               || character is '-' or '_' or '.' or ':');

    private sealed record FeishuImageDebugRoute(
        ChatExecutionCommandEntity Command,
        Dictionary<string, string> Metadata,
        string ConnectorId,
        string ExternalConversationId,
        string ExternalMessageId);

    private sealed record RouteResolution(
        int StatusCode,
        string? Error,
        FeishuImageDebugRoute? Route)
    {
        public static RouteResolution Fail(int statusCode, string error)
            => new(statusCode, error, null);

        public static RouteResolution Ok(FeishuImageDebugRoute route)
            => new(200, null, route);
    }
}

public sealed record FeishuImageDebugRequest
{
    public string? Prompt { get; init; }
    public string? Mode { get; init; }
    public string? Size { get; init; }
    public bool? Watermark { get; init; }
    public string? OutputFormat { get; init; }
    public string? OptimizePromptMode { get; init; }
    public bool? EnableWebSearch { get; init; }
    public int? ImageCount { get; init; }
    public List<string>? ReferenceArtifactIds { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public bool ConfirmSend { get; init; }
    public string? SourceCommandId { get; init; }
}
