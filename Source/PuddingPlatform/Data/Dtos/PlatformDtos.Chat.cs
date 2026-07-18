using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Data.Dtos;

// ════════════════════════════════════════════════════════════════
// Legacy realtime-session DTOs.
// Canonical Conversation Turn HTTP contracts live in PuddingCore/Platform.
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Legacy Omni realtime session input.
/// Not exposed by the canonical Conversation Turn endpoint.
/// </summary>
/// <remarks>
/// This model remains only for the dedicated Omni realtime adapter. It must not
/// be reused as a second HTTP chat command contract.
/// </remarks>
public record AdminChatRequest(
    /// <summary>用户消息正文。</summary>
    string MessageText,
    /// <summary>原始消息文本（视觉推理场景下 camera 拍照含图，此字段存纯文本）。</summary>
    string? OriginalMessageText,
    /// <summary>会话 ID；null 或 "main" → 自动解析主会话。</summary>
    string? SessionId,
    /// <summary>目标 Agent ID（路由后由 ChatRoomRouteResolver 填充）。</summary>
    string? AgentId,
    /// <summary>多 Agent 分发目标 ID 列表。</summary>
    IReadOnlyList<string>? TargetAgentIds = null,
    /// <summary>分发策略："agent"（单目标）| "all"（全部）。</summary>
    string? Audience = null,
    /// <summary>是否抑制用户端消息持久化到 transcript。</summary>
    bool SuppressUserTranscript = false,
    /// <summary>是否强制创建新会话。</summary>
    bool ForceNewSession = false,
    /// <summary>附加元数据（如 inputMode=camera）。</summary>
    IReadOnlyDictionary<string, string>? Metadata = null,
    /// <summary>ADR-058: 前端生成的幂等键；重试时复用，后端按 (workspace_id, client_request_id) 去重。</summary>
    string? ClientRequestId = null
);

/// <summary>Legacy synchronous realtime-session response.</summary>
public record AdminChatResponse(
    string MessageId,
    string SessionId,
    string? Reply,
    bool IsSuccess,
    string? ErrorMessage,
    TokenUsageDto? Usage,
    IReadOnlyList<TurnStepDto>? TurnSteps
);

/// <summary>
/// Runtime Steering 引导请求 — 注入到正在运行的 Agent 循环中。
/// 绕过正常 busy-session 消息门禁。
/// </summary>
public record AdminChatSteeringRequest(
    /// <summary>引导消息内容。</summary>
    string MessageText,
    /// <summary>目标 Agent ID。</summary>
    string? AgentId = null,
    /// <summary>关联的队列项 ID（可选）。</summary>
    string? SourceQueueItemId = null,
    /// <summary>优先级（越大越先处理）。</summary>
    int Priority = 100
);

/// <summary>Steering 创建响应。</summary>
public record AdminChatSteeringResponse(
    string SteeringId,
    string SessionId,
    string WorkspaceId,
    string? AgentId,
    string Status,
    long CreatedAt
);

/// <summary>视觉推理构件上传响应（Camera 路径）。</summary>
public record VisionArtifactUploadResponse(
    string ArtifactId,
    string MimeType,
    int? Width,
    int? Height,
    long CapturedAt
);
