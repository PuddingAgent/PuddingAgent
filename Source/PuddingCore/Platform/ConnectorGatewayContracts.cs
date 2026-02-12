using System.Text.Json.Serialization;

namespace PuddingCode.Platform;

/// <summary>
/// 连接器 → 网关/事件系统 的统一契约。
/// 目标：
///   1) 所有协议连接器（WebSocket/MQTT/HTTP...）只负责消息接入
///   2) 统一映射为 canonical eventType + payload
///   3) Agent/会话层无需理解外部协议细节
/// </summary>
public static class ConnectorGatewayContracts
{
    /// <summary>事件类型前缀。</summary>
    public const string EventPrefix = "connector";

    /// <summary>
    /// 生成标准事件类型：connector.{channelType}.{messageType}
    /// 例：connector.websocket.chat / connector.mqtt.sensor
    /// </summary>
    public static string BuildEventType(string channelType, string? messageType)
    {
        var c = NormalizeSegment(channelType, fallback: "unknown");
        var m = NormalizeSegment(messageType, fallback: "message");
        return $"{EventPrefix}.{c}.{m}";
    }

    /// <summary>规范化 eventType segment：仅保留 [a-z0-9_.-]，其余替换为 '-'</summary>
    public static string NormalizeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var chars = value.Trim().ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            var valid = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch is '.' or '_' or '-';
            if (!valid) chars[i] = '-';
        }
        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}

/// <summary>
/// 连接器入站消息的标准 payload（写入 InternalEvent.Payload）。
/// </summary>
public sealed record ConnectorInboundPayload
{
    [JsonPropertyName("channelId")]
    public required string ChannelId { get; init; }

    [JsonPropertyName("channelType")]
    public required string ChannelType { get; init; }

    [JsonPropertyName("userExternalId")]
    public required string UserExternalId { get; init; }

    [JsonPropertyName("messageText")]
    public required string MessageText { get; init; }

    [JsonPropertyName("messageType")]
    public string? MessageType { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = [];
}
