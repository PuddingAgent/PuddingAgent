namespace PuddingCode.Platform;

/// <summary>
/// 用户消息内容片段（ADR-077）。type=text 只接受 Text；type=image 只接受
/// Workspace Artifact 引用 ArtifactId + 可选 Detail。客户端 Data URL、外部 URL、
/// MIME 声明和本地路径都不是受控字段——图片必须先经 Artifact API 上传取得 artifactId。
/// </summary>
public sealed record ContentPart
{
    /// <summary>内容类型：text | image。</summary>
    public required string Type { get; init; }
    /// <summary>文本内容（type=text 时必填非空）。</summary>
    public string? Text { get; init; }
    /// <summary>Workspace vision Artifact 引用（type=image 时必填，格式 vision-&lt;32hex&gt;）。</summary>
    public string? ArtifactId { get; init; }
    /// <summary>图片 detail：original（默认）| low。</summary>
    public string? Detail { get; init; }
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
