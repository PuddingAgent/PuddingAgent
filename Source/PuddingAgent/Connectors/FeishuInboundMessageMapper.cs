using System.Security.Cryptography;
using System.Text;
using HarnessAgent.Core.Connectors.Feishu;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingAgent.Connectors;

/// <summary>
/// Materializes a verified Feishu event before Gateway acknowledgement. Image
/// resources are downloaded into the same durable Vision Artifact store used
/// by Web messages, so canonical history and Agent execution share one asset.
/// </summary>
public sealed class FeishuInboundMessageMapper(
    VisionArtifactStorageService artifactStorage,
    ILogger<FeishuInboundMessageMapper> logger)
{
    public async Task<PuddingIngressEnvelope> MapAsync(
        FeishuConnectorBinding binding,
        string connectorId,
        FeishuEvent evt,
        FeishuClient client,
        CancellationToken ct = default)
    {
        var senderId = evt.ExtractSenderId() ?? "feishu:anonymous";
        var chatId = evt.ExtractChatId();
        var messageId = evt.ExtractMessageId();
        var messageType = evt.Event?.Message?.MessageType?.Trim().ToLowerInvariant()
            ?? "unknown";

        if (string.IsNullOrWhiteSpace(chatId))
            throw new InvalidOperationException("Feishu inbound message is missing chat_id.");
        if (string.IsNullOrWhiteSpace(messageId))
            throw new InvalidOperationException("Feishu inbound message is missing message_id.");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "feishu",
            ["message_id"] = messageId,
            ["chat_id"] = chatId,
            ["sender_id"] = senderId,
            ["feishu_message_type"] = messageType,
            [MessageGatewayMetadata.TtsRepliesEnabled] =
                binding.TtsRepliesEnabled ? "true" : "false",
            [MessageGatewayMetadata.TtsVoice] =
                string.IsNullOrWhiteSpace(binding.TtsVoice)
                    ? "Cherry"
                    : binding.TtsVoice,
        };

        var text = evt.ExtractText();
        var gatewayMessageType = "chat";
        if (string.Equals(messageType, "image", StringComparison.Ordinal))
        {
            var imageKey = evt.ExtractImageKey();
            if (string.IsNullOrWhiteSpace(imageKey))
            {
                throw new InvalidOperationException(
                    "Feishu image message is missing image_key.");
            }

            var artifactId = StableArtifactId(connectorId, messageId, imageKey);
            var existing = await artifactStorage.ResolveLocalFileAsync(
                binding.WorkspaceId,
                artifactId,
                ct);
            if (existing is null)
            {
                var resource = await client.DownloadMessageResourceAsync(
                    messageId,
                    imageKey,
                    "image",
                    ct);
                var mimeType = DetectProviderSafeImageMimeType(resource.Content);
                await using var stream = new MemoryStream(
                    resource.Content,
                    writable: false);
                await artifactStorage.SaveIdempotentAsync(
                    binding.WorkspaceId,
                    artifactId,
                    stream,
                    mimeType,
                    capturedAt: ParseCreateTime(evt.Event?.Message?.CreateTime),
                    ct: ct);
            }

            metadata["inputMode"] = "image";
            metadata["visionArtifactId"] = artifactId;
            metadata["visionArtifactIds"] = artifactId;
            text = "用户从飞书发送了一张图片。";
            gatewayMessageType = "image";

            logger.LogInformation(
                "[Feishu] Image materialized connector={ConnectorId} message={MessageId} artifact={ArtifactId}",
                connectorId,
                messageId,
                artifactId);
        }

        return new PuddingIngressEnvelope
        {
            ConnectorId = connectorId,
            WorkspaceId = binding.WorkspaceId,
            AgentId = binding.AgentId,
            ChannelId = binding.ChannelId ?? "feishu",
            ChannelType = "feishu",
            UserExternalId = senderId,
            MessageText = text,
            MessageType = gatewayMessageType,
            ExternalConversationId = chatId,
            ExternalMessageId = messageId,
            CorrelationId = chatId,
            Metadata = metadata,
        };
    }

    private static string StableArtifactId(
        string connectorId,
        string messageId,
        string resourceKey)
    {
        var raw = Encoding.UTF8.GetBytes(
            $"feishu-image\n{connectorId}\n{messageId}\n{resourceKey}");
        var hash = SHA256.HashData(raw);
        return $"vision-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static long? ParseCreateTime(string? value)
    {
        if (!long.TryParse(value, out var timestamp) || timestamp <= 0)
            return null;

        // Feishu currently emits millisecond timestamps, but tolerate seconds
        // from older fixtures without persisting a 1970 capture time.
        return timestamp < 10_000_000_000 ? timestamp * 1000 : timestamp;
    }

    private static string DetectProviderSafeImageMimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3
            && bytes[0] == 0xFF
            && bytes[1] == 0xD8
            && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47
            && bytes[4] == 0x0D
            && bytes[5] == 0x0A
            && bytes[6] == 0x1A
            && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        throw new UnsupportedVisionArtifactMediaTypeException(
            "unknown Feishu image payload");
    }
}
