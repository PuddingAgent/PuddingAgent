using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.MessageGateway;

namespace PuddingAgent.Services;

/// <summary>
/// Projects committed Conversation content deltas to a Feishu CardKit stream.
/// Deltas are best-effort presentation updates; the committed terminal reply is
/// still emitted as a durable Connector delivery that finalizes the same card.
/// </summary>
public sealed class FeishuStreamingProjectionWorker(
    IServiceScopeFactory scopeFactory,
    ConnectorHost connectorHost,
    ChannelConfigurationFileService channels,
    ILogger<FeishuStreamingProjectionWorker> logger) : BackgroundService
{
    private const string ChannelType = "feishu";
    private const string ElementId = "stream_md";
    private const int MaxOperationAttempts = 5;
    private const int MaxStreamingContentBytes = 24 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var projected = await ProjectBatchAsync(stoppingToken);
                await Task.Delay(
                    projected > 0
                        ? TimeSpan.FromMilliseconds(100)
                        : TimeSpan.FromMilliseconds(300),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[FeishuStream] Projection loop failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public async Task<int> ProjectBatchAsync(CancellationToken ct = default)
    {
        List<string> commandIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            commandIds = await db.ChatExecutionCommands
                .AsNoTracking()
                .Where(command =>
                    command.MetadataJson != null
                    && command.MetadataJson.Contains(MessageGatewayMetadata.IsGatewayIngress))
                .Where(command =>
                    command.Status == "running"
                    || (command.Status == "succeeded"
                        && command.TerminalSequence != null
                        && command.ReplyProjectedAt == null)
                    || (command.TerminalSequence != null
                        && command.Status != "succeeded"
                        && db.ConnectorStreamProjections.Any(projection =>
                            projection.CommandId == command.CommandId
                            && projection.Status != ConnectorStreamProjectionStatuses.Completed
                            && projection.Status != ConnectorStreamProjectionStatuses.Failed)))
                .OrderBy(command => command.CreatedAt)
                .Select(command => command.CommandId)
                .Take(20)
                .ToListAsync(ct);
        }

        var projected = 0;
        foreach (var commandId in commandIds)
        {
            ct.ThrowIfCancellationRequested();
            if (await ProcessCommandAsync(commandId, ct))
                projected++;
        }

        return projected;
    }

    private async Task<bool> ProcessCommandAsync(
        string commandId,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var command = await db.ChatExecutionCommands
            .SingleOrDefaultAsync(item => item.CommandId == commandId, ct);
        if (command is null)
            return false;

        var metadata = DeserializeMetadata(command.MetadataJson);
        if (!IsTrue(Get(metadata, MessageGatewayMetadata.IsGatewayIngress)))
            return false;
        if (!string.Equals(
                Get(metadata, MessageGatewayMetadata.ChannelType),
                ChannelType,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var connectorId = Get(metadata, MessageGatewayMetadata.ConnectorId);
        var externalConversationId = Get(
            metadata,
            MessageGatewayMetadata.ExternalConversationId);
        var externalMessageId = Get(
            metadata,
            MessageGatewayMetadata.ExternalMessageId);
        if (connectorId is null
            || externalConversationId is null
            || externalMessageId is null)
        {
            return false;
        }

        var projection = await db.ConnectorStreamProjections
            .SingleOrDefaultAsync(
                item => item.CommandId == command.CommandId
                        && item.ConnectorId == connectorId,
                ct);

        if (projection is null)
        {
            if (!string.Equals(command.Status, "running", StringComparison.Ordinal))
                return false;

            var channelId = Get(metadata, MessageGatewayMetadata.ChannelId);
            var channel = channelId is null
                ? null
                : await channels.GetChannelAsync(channelId, ct);
            if (channel is not
                {
                    IsEnabled: true,
                    Feishu.StreamingRepliesEnabled: true,
                })
            {
                return false;
            }
            if (IsTrue(Get(
                    metadata,
                    MessageGatewayMetadata.VoiceToolSuppressFinalText)))
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            projection = new ConnectorStreamProjectionEntity
            {
                ProjectionId = StableId(
                    "connector-stream",
                    command.CommandId,
                    connectorId,
                    externalMessageId),
                CommandId = command.CommandId,
                WorkspaceId = command.WorkspaceId,
                ConversationId = command.SessionId,
                MessageId = command.MessageId,
                ConnectorId = connectorId,
                ExternalConversationId = externalConversationId,
                ExternalMessageId = externalMessageId,
                ElementId = ElementId,
                Status = ConnectorStreamProjectionStatuses.Starting,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ConnectorStreamProjections.Add(projection);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "[FeishuStream] Projection created command={CommandId} projection={ProjectionId}",
                command.CommandId,
                projection.ProjectionId);
        }

        if (projection.Status is ConnectorStreamProjectionStatuses.Completed
            or ConnectorStreamProjectionStatuses.Failed)
        {
            return false;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (projection.AvailableAt is { } availableAt && availableAt > nowMs)
            return false;

        try
        {
            var changed = await EnsurePublishedAsync(db, projection, ct);
            if (projection.Status != ConnectorStreamProjectionStatuses.Active
                && projection.Status != ConnectorStreamProjectionStatuses.Finalizing)
            {
                return changed;
            }

            if (projection.Status == ConnectorStreamProjectionStatuses.Active)
            {
                changed |= await ProjectContentAsync(db, projection, ct);
            }

            if (string.Equals(command.Status, "running", StringComparison.Ordinal))
                return changed;

            if (string.Equals(command.Status, "succeeded", StringComparison.Ordinal)
                && command.TerminalSequence is not null)
            {
                changed |= await QueueFinalDeliveryAsync(
                    scope.ServiceProvider,
                    db,
                    command,
                    projection,
                    metadata,
                    ct);
                return changed;
            }

            if (command.TerminalSequence is not null)
            {
                changed |= await CloseInterruptedAsync(
                    db,
                    command,
                    projection,
                    ct);
            }

            return changed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(db, projection, ex, ct);
            return true;
        }
    }

    private async Task<bool> EnsurePublishedAsync(
        PlatformDbContext db,
        ConnectorStreamProjectionEntity projection,
        CancellationToken ct)
    {
        var changed = false;
        if (projection.Status == ConnectorStreamProjectionStatuses.Starting)
        {
            var result = await RequireSuccessAsync(
                projection.ConnectorId,
                ConnectorStreamOperations.Create,
                new Dictionary<string, string>
                {
                    [ConnectorStreamParameters.Content] = "正在思考…",
                },
                ct);
            projection.ExternalResourceId = RequireData(
                result,
                ConnectorStreamOperations.Create);
            projection.Status = ConnectorStreamProjectionStatuses.ResourceCreated;
            ResetFailure(projection);
            projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(ct);
            changed = true;
        }

        if (projection.Status == ConnectorStreamProjectionStatuses.ResourceCreated)
        {
            var result = await RequireSuccessAsync(
                projection.ConnectorId,
                ConnectorStreamOperations.Publish,
                new Dictionary<string, string>
                {
                    [ConnectorStreamParameters.ResourceId] =
                        RequireResourceId(projection),
                    [ConnectorStreamParameters.ExternalMessageId] =
                        projection.ExternalMessageId,
                    [ConnectorStreamParameters.Uuid] = StableId(
                        "stream-publish",
                        projection.ProjectionId,
                        projection.ExternalMessageId),
                },
                ct);
            projection.ExternalReplyId = RequireData(
                result,
                ConnectorStreamOperations.Publish);
            projection.Status = ConnectorStreamProjectionStatuses.Active;
            ResetFailure(projection);
            projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "[FeishuStream] Card published command={CommandId} projection={ProjectionId}",
                projection.CommandId,
                projection.ProjectionId);
            changed = true;
        }

        return changed;
    }

    private async Task<bool> ProjectContentAsync(
        PlatformDbContext db,
        ConnectorStreamProjectionEntity projection,
        CancellationToken ct)
    {
        if (projection.PendingEventSequence is null)
        {
            var events = await db.ConversationEvents
                .AsNoTracking()
                .Where(evt =>
                    evt.CommandId == projection.CommandId
                    && evt.Type == ConversationEventTypes.MessageContentAppended
                    && evt.Sequence > projection.LastEventSequence)
                .OrderBy(evt => evt.Sequence)
                .Take(100)
                .ToListAsync(ct);
            if (events.Count == 0)
                return false;

            var appended = string.Concat(events.Select(ReadDelta));
            if (Encoding.UTF8.GetByteCount(projection.Content)
                + Encoding.UTF8.GetByteCount(appended) > MaxStreamingContentBytes)
            {
                throw new InvalidOperationException(
                    $"Feishu streaming content exceeded {MaxStreamingContentBytes} UTF-8 bytes; " +
                    "the committed terminal reply will fall back to text.");
            }

            projection.Content += appended;
            projection.PendingEventSequence = events[^1].Sequence;
            projection.OperationSequence++;
            projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(ct);
        }

        var pending = projection.PendingEventSequence!.Value;
        await RequireSuccessAsync(
            projection.ConnectorId,
            ConnectorStreamOperations.Update,
            new Dictionary<string, string>
            {
                [ConnectorStreamParameters.ResourceId] = RequireResourceId(projection),
                [ConnectorStreamParameters.ElementId] = projection.ElementId,
                [ConnectorStreamParameters.Content] = projection.Content,
                [ConnectorStreamParameters.Sequence] =
                    projection.OperationSequence.ToString(),
                [ConnectorStreamParameters.Uuid] = StableId(
                    "stream-update",
                    projection.ProjectionId,
                    projection.OperationSequence.ToString()),
            },
            ct);

        projection.LastEventSequence = pending;
        projection.PendingEventSequence = null;
        ResetFailure(projection);
        projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        logger.LogDebug(
            "[FeishuStream] Content projected command={CommandId} eventSequence={EventSequence} operationSequence={OperationSequence} chars={Chars}",
            projection.CommandId,
            projection.LastEventSequence,
            projection.OperationSequence,
            projection.Content.Length);
        return true;
    }

    private async Task<bool> QueueFinalDeliveryAsync(
        IServiceProvider services,
        PlatformDbContext db,
        ChatExecutionCommandEntity command,
        ConnectorStreamProjectionEntity projection,
        Dictionary<string, string> metadata,
        CancellationToken ct)
    {
        if (projection.PendingEventSequence is not null)
            return false;

        var terminalEvent = await db.ConversationEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                evt => evt.ConversationId == command.SessionId
                       && evt.Sequence == command.TerminalSequence,
                ct);
        var rawReply = terminalEvent is null ? null : ReadReply(terminalEvent.Payload);
        if (string.IsNullOrWhiteSpace(rawReply))
            return false;
        var plan = FeishuTtsProjection.CreatePlan(
            command.Status,
            rawReply,
            metadata);
        var reply = plan.TextContent ?? string.Empty;
        var replyBytes = Encoding.UTF8.GetByteCount(reply);
        if (replyBytes > MaxStreamingContentBytes)
        {
            projection.Status = ConnectorStreamProjectionStatuses.Failed;
            projection.LastError =
                $"Terminal reply exceeded {MaxStreamingContentBytes} UTF-8 bytes.";
            projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "[FeishuStream] Terminal reply too long; falling back to text command={CommandId} bytes={Bytes}",
                command.CommandId,
                replyBytes);
            return true;
        }

        if (projection.Status == ConnectorStreamProjectionStatuses.Active)
        {
            projection.OperationSequence += 2;
            projection.Status = ConnectorStreamProjectionStatuses.Finalizing;
            projection.Content = reply;
            projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(ct);
        }

        if (projection.Status != ConnectorStreamProjectionStatuses.Finalizing)
            return false;

        var externalMessageId = Get(
            metadata,
            MessageGatewayMetadata.ExternalMessageId);
        var replyMessageId = StableId(
            "gateway-reply",
            command.CommandId,
            projection.ConnectorId,
            externalMessageId ?? projection.ExternalConversationId);
        metadata[MessageGatewayMetadata.ReplyProjectedMessageId] = replyMessageId;
        metadata[MessageGatewayMetadata.IdempotencyKey] = replyMessageId;
        metadata[ConnectorStreamMetadata.ReplyMode] =
            ConnectorStreamMetadata.FinalizeReplyMode;
        metadata[ConnectorStreamMetadata.ProjectionId] = projection.ProjectionId;
        metadata[ConnectorStreamMetadata.ResourceId] = RequireResourceId(projection);
        metadata[ConnectorStreamMetadata.ElementId] = projection.ElementId;
        metadata[ConnectorStreamMetadata.ContentSequence] =
            (projection.OperationSequence - 1).ToString();
        metadata[ConnectorStreamMetadata.FinishSequence] =
            projection.OperationSequence.ToString();

        var messageSystem = services.GetRequiredService<IMessageSystem>();
        if (!string.IsNullOrWhiteSpace(plan.TextContent))
        {
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
                            Id = projection.ConnectorId,
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
                    Content = plan.TextContent,
                    Metadata = metadata,
                },
                ct);
        }
        string? ttsMessageId = null;
        if (!string.IsNullOrWhiteSpace(plan.VoiceContent))
        {
            ttsMessageId = await FeishuTtsProjection.QueueAsync(
                messageSystem,
                command.CommandId,
                command.WorkspaceId,
                command.AgentInstanceId,
                projection.ConnectorId,
                command.SessionId,
                command.TurnId,
                externalMessageId,
                projection.ExternalConversationId,
                plan.VoiceContent,
                metadata,
                ct);
        }
        command.ReplyProjectedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "[FeishuStream] Final delivery projected command={CommandId} projection={ProjectionId} message={MessageId} voiceDirective={VoiceDirective} ttsMessage={TtsMessageId}",
            command.CommandId,
            projection.ProjectionId,
            string.IsNullOrWhiteSpace(plan.TextContent) ? null : replyMessageId,
            plan.HasVoiceDirective,
            ttsMessageId);
        return true;
    }

    private async Task<bool> CloseInterruptedAsync(
        PlatformDbContext db,
        ChatExecutionCommandEntity command,
        ConnectorStreamProjectionEntity projection,
        CancellationToken ct)
    {
        if (projection.Status is not (
                ConnectorStreamProjectionStatuses.Active
                or ConnectorStreamProjectionStatuses.Finalizing))
            return false;

        var terminalEvent = command.TerminalSequence is null
            ? null
            : await db.ConversationEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    evt => evt.ConversationId == command.SessionId
                           && evt.Sequence == command.TerminalSequence,
                    ct);
        var presentation = terminalEvent is null
            ? null
            : ConversationTerminalMessageFormatter.Parse(terminalEvent.Payload);
        var content = presentation?.Content
                      ?? (string.IsNullOrWhiteSpace(projection.Content)
                          ? "生成中断。"
                          : $"{projection.Content}\n\n— 生成中断");
        var summary = presentation?.Summary ?? "生成中断";

        if (projection.Status == ConnectorStreamProjectionStatuses.Active)
        {
            projection.OperationSequence += 2;
            projection.Status = ConnectorStreamProjectionStatuses.Finalizing;
        }
        projection.Content = content;
        projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);

        await RequireSuccessAsync(
            projection.ConnectorId,
            ConnectorStreamOperations.Update,
            new Dictionary<string, string>
            {
                [ConnectorStreamParameters.ResourceId] = RequireResourceId(projection),
                [ConnectorStreamParameters.ElementId] = projection.ElementId,
                [ConnectorStreamParameters.Content] = projection.Content,
                [ConnectorStreamParameters.Sequence] =
                    (projection.OperationSequence - 1).ToString(),
                [ConnectorStreamParameters.Uuid] = StableId(
                    "stream-interrupted-content",
                    projection.ProjectionId),
            },
            ct);
        await RequireSuccessAsync(
            projection.ConnectorId,
            ConnectorStreamOperations.Finish,
            new Dictionary<string, string>
            {
                [ConnectorStreamParameters.ResourceId] = RequireResourceId(projection),
                [ConnectorStreamParameters.Content] = projection.Content,
                [ConnectorStreamParameters.Summary] = summary,
                [ConnectorStreamParameters.Sequence] =
                    projection.OperationSequence.ToString(),
                [ConnectorStreamParameters.Uuid] = StableId(
                    "stream-interrupted-finish",
                    projection.ProjectionId),
            },
            ct);

        projection.Status = ConnectorStreamProjectionStatuses.Completed;
        command.ReplyProjectedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ResetFailure(projection);
        projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<ConnectorOperationResult> RequireSuccessAsync(
        string connectorId,
        string operation,
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        var result = await connectorHost.OperateAsync(
            connectorId,
            operation,
            parameters,
            ct);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                result.Error ?? $"Connector operation '{operation}' failed.");
        }

        return result;
    }

    private async Task RecordFailureAsync(
        PlatformDbContext db,
        ConnectorStreamProjectionEntity projection,
        Exception error,
        CancellationToken ct)
    {
        projection.AttemptCount++;
        projection.LastError = error.Message.Length <= 1000
            ? error.Message
            : error.Message[..1000];
        projection.AvailableAt = DateTimeOffset.UtcNow
            .AddSeconds(Math.Min(10, 1 << Math.Min(4, projection.AttemptCount - 1)))
            .ToUnixTimeMilliseconds();
        projection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (projection.AttemptCount >= MaxOperationAttempts)
        {
            projection.Status = ConnectorStreamProjectionStatuses.Failed;
            projection.AvailableAt = null;
        }

        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            error,
            "[FeishuStream] Projection operation failed command={CommandId} projection={ProjectionId} status={Status} attempt={Attempt}",
            projection.CommandId,
            projection.ProjectionId,
            projection.Status,
            projection.AttemptCount);
    }

    private static void ResetFailure(ConnectorStreamProjectionEntity projection)
    {
        projection.AttemptCount = 0;
        projection.AvailableAt = null;
        projection.LastError = null;
    }

    private static string RequireResourceId(
        ConnectorStreamProjectionEntity projection)
        => !string.IsNullOrWhiteSpace(projection.ExternalResourceId)
            ? projection.ExternalResourceId
            : throw new InvalidOperationException(
                $"Connector stream projection '{projection.ProjectionId}' has no resource id.");

    private static string RequireData(
        ConnectorOperationResult result,
        string operation)
        => !string.IsNullOrWhiteSpace(result.Data)
            ? result.Data
            : throw new InvalidOperationException(
                $"Connector operation '{operation}' returned no resource identity.");

    private static string ReadDelta(ConversationEventEntity evt)
    {
        try
        {
            using var document = JsonDocument.Parse(evt.Payload);
            return document.RootElement.TryGetProperty("delta", out var delta)
                   && delta.ValueKind == JsonValueKind.String
                ? delta.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
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

    private static string StableId(params string[] parts)
    {
        var raw = string.Join('\n', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
    }
}
