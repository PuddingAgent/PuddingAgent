using System.Security.Cryptography;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.MessageGateway;

/// <summary>Creates durable, independently retryable Feishu image deliveries.</summary>
public static class FeishuImageProjection
{
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
        string artifactId,
        IReadOnlyDictionary<string, string> sourceMetadata,
        CancellationToken ct = default)
    {
        var messageId = StableId(
            "gateway-reply-image",
            stableSourceId,
            connectorId,
            externalMessageId ?? externalConversationId,
            artifactId);
        var metadata = new Dictionary<string, string>(
            sourceMetadata,
            StringComparer.Ordinal)
        {
            [MessageGatewayMetadata.ReplyProjectedMessageId] = messageId,
            [MessageGatewayMetadata.IdempotencyKey] = messageId,
            [ConnectorPayloadMetadata.Kind] = ConnectorPayloadKinds.VisionImage,
            [ConnectorPayloadMetadata.ArtifactId] = artifactId,
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
                ContentType = MessageContentTypes.Image,
                Content = artifactId,
                Metadata = metadata,
            },
            ct);
        return messageId;
    }

    private static void RemoveStreamMetadata(
        IDictionary<string, string> metadata)
    {
        metadata.Remove(ConnectorStreamMetadata.ReplyMode);
        metadata.Remove(ConnectorStreamMetadata.ProjectionId);
        metadata.Remove(ConnectorStreamMetadata.ResourceId);
        metadata.Remove(ConnectorStreamMetadata.ElementId);
        metadata.Remove(ConnectorStreamMetadata.ContentSequence);
        metadata.Remove(ConnectorStreamMetadata.FinishSequence);
    }

    private static string StableId(params string[] parts)
    {
        var raw = string.Join('\n', parts);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
    }
}
