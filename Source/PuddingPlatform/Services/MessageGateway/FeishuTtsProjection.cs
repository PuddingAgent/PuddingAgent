using System.Security.Cryptography;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.MessageGateway;

/// <summary>
/// Parses explicit Agent voice intent and creates durable, independently
/// retryable Feishu audio deliveries.
/// </summary>
public static class FeishuTtsProjection
{
    public const int MaxTextCharacters = 1_000;

    public static FeishuReplyProjectionPlan CreatePlan(
        string commandStatus,
        string content,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (!string.Equals(commandStatus, "succeeded", StringComparison.Ordinal))
            return FeishuReplyProjectionPlan.TextOnly(content);

        var directive = AgentReplyVoiceDirective.Parse(content);
        if (!directive.HasVoice)
            return FeishuReplyProjectionPlan.TextOnly(content);

        if (!IsTrue(Get(metadata, MessageGatewayMetadata.TtsRepliesEnabled))
            || directive.VoiceContent.Length > MaxTextCharacters)
        {
            return FeishuReplyProjectionPlan.TextOnly(content);
        }

        return new FeishuReplyProjectionPlan(
            string.IsNullOrWhiteSpace(content) ? null : content.Trim(),
            directive.VoiceContent,
            HasVoiceDirective: true);
    }

    public static async Task<string> QueueAsync(
        IMessageSystem messageSystem,
        string stableSourceId,
        string workspaceId,
        string agentInstanceId,
        string connectorId,
        string conversationId,
        string turnId,
        string? externalMessageId,
        string externalConversationId,
        string content,
        IReadOnlyDictionary<string, string> sourceMetadata,
        CancellationToken ct = default)
    {
        var messageId = StableId(
            "gateway-reply-tts",
            stableSourceId,
            connectorId,
            externalMessageId ?? externalConversationId);
        var metadata = new Dictionary<string, string>(
            sourceMetadata,
            StringComparer.Ordinal)
        {
            [MessageGatewayMetadata.ReplyProjectedMessageId] = messageId,
            [MessageGatewayMetadata.IdempotencyKey] = messageId,
            [ConnectorPayloadMetadata.Kind] = ConnectorPayloadKinds.TtsAudio,
        };
        RemoveStreamMetadata(metadata);

        await messageSystem.SendAsync(
            new MessageEnvelope
            {
                MessageId = messageId,
                From = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = agentInstanceId,
                    WorkspaceId = workspaceId,
                },
                To =
                [
                    new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Connector,
                        Id = connectorId,
                        WorkspaceId = workspaceId,
                    },
                ],
                RoomId = conversationId,
                ConversationId = conversationId,
                ReplyToMessageId = externalMessageId,
                CorrelationId = conversationId,
                CausationId = turnId,
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.System,
                ContentType = MessageContentTypes.Audio,
                Content = content,
                Metadata = metadata,
            },
            ct);
        return messageId;
    }

    private static void RemoveStreamMetadata(IDictionary<string, string> metadata)
    {
        metadata.Remove(ConnectorStreamMetadata.ReplyMode);
        metadata.Remove(ConnectorStreamMetadata.ProjectionId);
        metadata.Remove(ConnectorStreamMetadata.ResourceId);
        metadata.Remove(ConnectorStreamMetadata.ElementId);
        metadata.Remove(ConnectorStreamMetadata.ContentSequence);
        metadata.Remove(ConnectorStreamMetadata.FinishSequence);
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

public sealed record FeishuReplyProjectionPlan(
    string? TextContent,
    string? VoiceContent,
    bool HasVoiceDirective)
{
    public static FeishuReplyProjectionPlan TextOnly(string content)
        => new(
            string.IsNullOrWhiteSpace(content) ? null : content.Trim(),
            VoiceContent: null,
            HasVoiceDirective: false);
}
