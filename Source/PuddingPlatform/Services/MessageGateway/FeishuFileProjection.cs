using System.Security.Cryptography;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.MessageGateway;

/// <summary>
/// 为本地文件投递创建可独立重试的飞书出站消息。
///
/// <para>
/// 与 <see cref="FeishuImageProjection"/> 同构：只换载荷类型与素材键。
/// 图片走 vision artifact（ArtifactId），文件走磁盘路径（SourcePath）——
/// 两者不能共用一套解析，文件不是 artifact。
/// </para>
/// </summary>
public static class FeishuFileProjection
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
        string sourcePath,
        IReadOnlyDictionary<string, string> sourceMetadata,
        CancellationToken ct = default)
    {
        var messageId = StableId(
            "gateway-reply-file",
            stableSourceId,
            connectorId,
            externalMessageId ?? externalConversationId,
            sourcePath);
        var metadata = new Dictionary<string, string>(
            sourceMetadata,
            StringComparer.Ordinal)
        {
            [MessageGatewayMetadata.ReplyProjectedMessageId] = messageId,
            [MessageGatewayMetadata.IdempotencyKey] = messageId,
            [ConnectorPayloadMetadata.Kind] = ConnectorPayloadKinds.WorkspaceFile,
            [ConnectorPayloadMetadata.SourcePath] = sourcePath,
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
                ContentType = MessageContentTypes.File,
                Content = sourcePath,
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

    private static string StableId(params string[] parts)
    {
        var raw = string.Join('\n', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
    }
}
