namespace PuddingCode.Platform;

/// <summary>
/// Connector 入站进入 Pudding canonical Conversation 的唯一网关入口。
/// V1 仅飞书使用此入口，但契约不依赖飞书 SDK。
/// </summary>
public interface IMessageGatewayIngress
{
    Task<MessageGatewayIngressResult> AcceptAsync(
        PuddingIngressEnvelope envelope,
        CancellationToken ct = default);
}

public sealed record MessageGatewayIngressResult
{
    public required string MessageId { get; init; }
    public required string ConversationId { get; init; }
    public required IReadOnlyList<string> DeliveryIds { get; init; }
}

/// <summary>
/// Message Gateway 在 Message Fabric 和 Conversation metadata 中使用的稳定键。
/// 外部协议字段必须与内部 channel/connector identity 分离。
/// </summary>
public static class MessageGatewayMetadata
{
    public const string IsGatewayIngress = "gateway_ingress";
    public const string ChannelId = "gateway_channel_id";
    public const string ChannelType = "gateway_channel_type";
    public const string ConnectorId = "gateway_connector_id";
    public const string ExternalConversationId = "gateway_external_conversation_id";
    public const string ExternalMessageId = "gateway_external_message_id";
    public const string ExternalUserId = "gateway_external_user_id";
    public const string MessageType = "gateway_message_type";
    public const string ClientRequestId = "gateway_client_request_id";
    public const string IsGatewayCommand = "gateway_command";
    public const string GatewayCommand = "gateway_command_text";
    public const string ReplyProjectedMessageId = "gateway_reply_message_id";
    public const string IdempotencyKey = "gateway_idempotency_key";
    public const string TtsRepliesEnabled = "gateway_tts_replies_enabled";
    public const string TtsVoice = "gateway_tts_voice";
    public const string VoiceToolSuppressFinalText =
        "gateway_voice_tool_suppress_final_text";
    public const string ImageToolSuppressDirective =
        "gateway_image_tool_suppress_directive";
    /// <summary>
    /// 标记投递来自工具/投影主动投递（如 send_message 飞书回信），而非终态答复投影。
    /// </summary>
    public const string IsProjection = "gateway_is_projection";
}

/// <summary>
/// Message Fabric delivery -> canonical Conversation Turn handoff metadata.
/// These keys are process-internal routing facts and must not be accepted from
/// ordinary HTTP SubmitTurn callers.
/// </summary>
public static class MessageFabricTurnMetadata
{
    public const string IsIngress = "message_fabric_ingress";
    public const string DeliveryId = "message_fabric_delivery_id";
    public const string MessageId = "message_fabric_message_id";
    public const string FromKind = "message_fabric_from_kind";
    public const string FromId = "message_fabric_from_id";
    public const string FromDisplayName = "message_fabric_from_display_name";
    public const string RoomId = "message_fabric_room_id";
    public const string ConversationId = "message_fabric_conversation_id";
    public const string ReplyToMessageId = "message_fabric_reply_to_message_id";
    public const string CorrelationId = "message_fabric_correlation_id";
    public const string CausationId = "message_fabric_causation_id";
    public const string Priority = "message_fabric_priority";
    public const string ReplyExpected = "message_fabric_reply_expected";
}
