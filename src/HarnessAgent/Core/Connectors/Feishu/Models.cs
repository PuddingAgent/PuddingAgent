namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 飞书应用配置。
/// </summary>
public class FeishuConfig
{
    public string AppId { get; init; } = "";
    public string AppSecret { get; init; } = "";
    public string? Description { get; init; }
    public string? Created { get; init; }
}

/// <summary>
/// Tenant access token 响应。
/// </summary>
public class TenantAccessTokenResponse
{
    public int Code { get; set; }
    public string? Msg { get; set; }
    public string? TenantAccessToken { get; set; }
    public int Expire { get; set; } // seconds
}

/// <summary>
/// 发送消息请求体。
/// </summary>
public class SendMessageRequest
{
    public string ReceiveId { get; set; } = "";
    public string MsgType { get; set; } = "text";
    public string Content { get; set; } = "";
    public string? Uuid { get; set; }
}

/// <summary>
/// 发送消息响应。
/// </summary>
public class SendMessageResponse
{
    public int Code { get; set; }
    public string? Msg { get; set; }
    public MessageData? Data { get; set; }
}

public class MessageData
{
    public string? MessageId { get; set; }
}

/// <summary>Feishu IM file upload response.</summary>
public class UploadFileResponse
{
    public int Code { get; set; }
    public string? Msg { get; set; }
    public UploadFileData? Data { get; set; }
}

public class UploadFileData
{
    public string? FileKey { get; set; }
}

/// <summary>CardKit create/update response.</summary>
public class CardKitResponse
{
    public int Code { get; set; }
    public string? Msg { get; set; }
    public CardKitData? Data { get; set; }
}

public class CardKitData
{
    public string? CardId { get; set; }
}

/// <summary>
/// 飞书事件基类（WebSocket 推送）。
/// </summary>
public class FeishuEvent
{
    public string? Schema { get; set; }         // 事件 schema URL
    public FeishuEventHeader? Header { get; set; }
    public FeishuEventV2? Event { get; set; }   // V2 格式
    public object? EventRaw { get; set; }        // V1 raw
}

public class FeishuEventHeader
{
    public string? EventId { get; set; }
    public string? EventType { get; set; }       // e.g. im.message.receive_v1
    public string? CreateTime { get; set; }
    public string? Token { get; set; }
    public string? AppId { get; set; }
    public string? TenantKey { get; set; }
}

/// <summary>
/// 飞书事件 V2 格式。
/// </summary>
public class FeishuEventV2
{
    public FeishuMessageEvent? Message { get; set; }
    public FeishuEventSender? Sender { get; set; }
}

/// <summary>
/// 消息事件的 message 部分。
/// </summary>
public class FeishuMessageEvent
{
    public string? MessageId { get; set; }
    public string? ChatId { get; set; }
    public string? ChatType { get; set; }       // p2p / group
    public string? MessageType { get; set; }     // text / image / ...
    public string? Content { get; set; }          // JSON string
    public string? Text { get; set; }             // text without at
    public string? TextWithoutAtBot { get; set; }
    public string? RootId { get; set; }
    public string? ParentId { get; set; }
    public string? CreateTime { get; set; }
}

public class FeishuEventSender
{
    public FeishuSenderId? SenderId { get; set; }
}

public class FeishuSenderId
{
    public string? UnionId { get; set; }
    public string? UserId { get; set; }
    public string? OpenId { get; set; }
}

/// <summary>
/// 文本消息内容（Content 字段 JSON 解析后的结构）。
/// </summary>
public class FeishuTextContent
{
    public string? Text { get; set; }
}

/// <summary>
/// 图片消息内容（Content 字段 JSON 解析后的结构）。
/// </summary>
public class FeishuImageContent
{
    public string? ImageKey { get; set; }
}

/// <summary>Downloaded message resource returned by Feishu OpenAPI.</summary>
public sealed record FeishuMessageResource(
    byte[] Content,
    string? ContentType,
    string? FileName);

/// <summary>
/// URL 验证事件（首次配置时）。
/// </summary>
public class FeishuUrlVerificationEvent : FeishuEvent
{
    public string? Challenge { get; set; }
}
