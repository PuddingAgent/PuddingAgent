using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Services.MessageGateway;

namespace PuddingAgent.Tools;

/// <summary>
/// Queues a durable voice reply for the current Feishu-originated Agent turn.
/// Routing is resolved from trusted command metadata; the model cannot choose
/// an arbitrary connector, external conversation, or message target.
/// </summary>
[Tool(
    id: "send_voice",
    name: "Send voice reply",
    description:
        "Send spoken text as a Feishu voice reply for the current Feishu turn. " +
        "The tool resolves the current reply target securely; do not provide a recipient. " +
        "After the voice is queued, Pudding suppresses the later final text reply.",
    category: ToolCategory.Messaging,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None,
    SubAgentExposure = SubAgentExposure.MainAgentOnly,
    SortOrder = 18)]
public sealed class SendVoiceTool(
    IServiceScopeFactory scopeFactory,
    ILogger<SendVoiceTool> logger) : PuddingToolBase<SendVoiceArgs>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SendVoiceArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var text = args.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return ToolExecutionResult.Fail("text is required.");
        if (text.Length > FeishuTtsProjection.MaxTextCharacters)
        {
            return ToolExecutionResult.Fail(
                $"Voice text must not exceed {FeishuTtsProjection.MaxTextCharacters} characters.");
        }

        var identity = context.ExecutionIdentity;
        if (identity is not
            {
                Kind: RuntimeExecutionKind.ConversationTurn,
                CommandId.Length: > 0,
                ToolCallId.Length: > 0,
            })
        {
            return ToolExecutionResult.Fail(
                "send_voice is only available inside a main conversation turn.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var messageSystem = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
        var command = await db.ChatExecutionCommands
            .SingleOrDefaultAsync(
                item => item.CommandId == identity.CommandId,
                ct);
        if (command is null
            || !string.Equals(
                command.WorkspaceId,
                context.WorkspaceId,
                StringComparison.Ordinal)
            || !string.Equals(
                command.AgentInstanceId,
                context.AgentInstanceId,
                StringComparison.Ordinal))
        {
            return ToolExecutionResult.Fail(
                "The current execution command could not be resolved safely.");
        }

        var metadata = DeserializeMetadata(command.MetadataJson);
        if (!IsTrue(Get(metadata, MessageGatewayMetadata.IsGatewayIngress))
            || !string.Equals(
                Get(metadata, MessageGatewayMetadata.ChannelType),
                "feishu",
                StringComparison.OrdinalIgnoreCase))
        {
            return ToolExecutionResult.Fail(
                "send_voice is only available for a Feishu-originated turn.");
        }
        if (!IsTrue(Get(
                metadata,
                MessageGatewayMetadata.TtsRepliesEnabled)))
        {
            return ToolExecutionResult.Fail(
                "Voice replies are disabled for the current Feishu channel.");
        }

        var hasPublishedStream = await db.ConnectorStreamProjections
            .AsNoTracking()
            .AnyAsync(
                projection =>
                    projection.CommandId == command.CommandId
                    && projection.Status != ConnectorStreamProjectionStatuses.Failed
                    && projection.Status != ConnectorStreamProjectionStatuses.Completed,
                ct);
        if (hasPublishedStream)
        {
            return ToolExecutionResult.Fail(
                "A streaming text reply has already started. Use a Markdown voice block " +
                "in the final reply so Pudding can preserve the text-then-voice order.");
        }

        var connectorId = Get(metadata, MessageGatewayMetadata.ConnectorId);
        var externalConversationId = Get(
            metadata,
            MessageGatewayMetadata.ExternalConversationId);
        var externalMessageId = Get(
            metadata,
            MessageGatewayMetadata.ExternalMessageId);
        if (connectorId is null || externalConversationId is null)
        {
            return ToolExecutionResult.Fail(
                "The current Feishu reply route is incomplete.");
        }

        var messageId = await FeishuTtsProjection.QueueAsync(
            messageSystem,
            $"{command.CommandId}:tool:{identity.ToolCallId}",
            command.WorkspaceId,
            command.AgentInstanceId,
            connectorId,
            command.SessionId,
            command.TurnId,
            externalMessageId,
            externalConversationId,
            text,
            metadata,
            ct);

        metadata[MessageGatewayMetadata.VoiceToolSuppressFinalText] = "true";
        command.MetadataJson = JsonSerializer.Serialize(metadata, JsonOptions);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[SendVoiceTool] Voice queued command={CommandId} toolCall={ToolCallId} message={MessageId}",
            command.CommandId,
            identity.ToolCallId,
            messageId);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(
            new
            {
                status = "queued",
                messageId,
                finalTextSuppressed = true,
            },
            JsonOptions));
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
}

public sealed record SendVoiceArgs
{
    [ToolParam("Text to synthesize and send as the Feishu voice message.")]
    public string? Text { get; init; }
}
