namespace PuddingCode.Platform;

/// <summary>
/// 用户消息内容片段 — 支持文本、图片、文件等多媒体类型。
/// 替代 AdminChatRequest.MessageText 纯文本字段。
/// </summary>
public sealed record ContentPart
{
    /// <summary>内容类型：text | image | file。</summary>
    public required string Type { get; init; }
    /// <summary>文本内容（type=text 时必填）。</summary>
    public string? Text { get; init; }
    /// <summary>MIME 类型（type=image/file 时）。</summary>
    public string? MimeType { get; init; }
    /// <summary>数据 URL 或 base64 payload（type=image/file 时）。</summary>
    public string? DataUrl { get; init; }
}

/// <summary>
/// 消息收件人定义 — 指定消息分发给哪些 Agent。
/// </summary>
public sealed record RecipientRequest
{
    /// <summary>分发类型：agent（指定 Agent）| all（工作区全部 Agent）。</summary>
    public required string Type { get; init; }
    /// <summary>目标 Agent ID 列表（type=agent 时必填）。</summary>
    public IReadOnlyList<string>? AgentIds { get; init; }
}

/// <summary>
/// 提交 Turn 请求 — POST /api/v1/conversations/{id}/turns 的 HTTP 载荷。
/// 不含 LLM/Tool/Skill 配置;不含 SSE Channel;不含 Trace 配置。
/// </summary>
public sealed record SubmitTurnRequest
{
    /// <summary>前端生成的幂等键；重试复用同一 ID，后端按 (workspace_id, client_request_id) 去重。</summary>
    public required string ClientRequestId { get; init; }
    /// <summary>前端生成的稳定用户消息 ID；用作 ChatMessageEntity.MessageId。</summary>
    public required string ClientMessageId { get; init; }
    /// <summary>收件人定义。</summary>
    public required RecipientRequest Recipients { get; init; }
    /// <summary>消息内容（至少一个 part）。</summary>
    public required IReadOnlyList<ContentPart> Content { get; init; }
    /// <summary>是否强制创建新会话。</summary>
    public bool ForceNewSession { get; init; }
    /// <summary>附加元数据。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// 受理结果 — Controller 映射后返回 HTTP 202 Accepted。
/// </summary>
public sealed record AcceptanceResult
{
    public required string ConversationId { get; init; }
    public required string MessageId { get; init; }
    public required IReadOnlyList<string> TurnIds { get; init; }
    public required IReadOnlyList<string> CommandIds { get; init; }
    public required long AcceptedSequence { get; init; }
}
