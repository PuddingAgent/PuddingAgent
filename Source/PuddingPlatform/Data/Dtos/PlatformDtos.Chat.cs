using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingPlatform.Data.Dtos;

// ════════════════════════════════════════════════════════════════
// Chat Proxy DTOs — 聊天消息 API 请求/响应。
// ADR-058: AdminChatRequest 不含 LLM/Tool/Skill 配置。
// ════════════════════════════════════════════════════════════════

/// <summary>
/// 聊天消息发送请求 — POST /api/workspaces/{ws}/chat/message。
/// </summary>
/// <remarks>
/// ADR-058: 不含 LlmConfig/CapabilityPolicy/ToolDefinitions/SkillPackages。
/// 前端只需提供消息内容、Agent 路由和幂等键。
///
/// <b>FIXME 清单：</b>
/// <list type="number">
/// <item>
/// <description>
///   <b>clientMessageId 缺失</b><br/>
///   前端 useChatState.ts sendMessage() 已生成 clientRequestId + clientMessageId，
///   但 buildChatMessageRequest() 未将 clientMessageId 写入 AdminChatRequest。
///   当前 ChatApiController 在服务端用 Guid 回退生成，
///   导致 HTTP 超时后重试产生两条不同的用户消息（两个 ChatMessageEntity 行）。
///   <br/><b>修复：</b>AdminChatRequest 增加 ClientMessageId 字段；
///   buildChatMessageRequest() 传入；后端优先使用，为 null 时回退。
///   <br/><b>影响：</b>幂等性破缺 → 用户可能看到重复消息。
/// </description>
/// </item>
/// <item>
/// <description>
///   <b>TargetAgentIds 多 Agent @all 广播未接入执行链</b><br/>
///   前端 @all 时 TargetAgentIds 包含所有 Agent，ChatApiController 为每个目标解析了
///   dispatch 信息（DisplayName/AvatarUrl/TemplateId），但 ChatCommandAcceptanceService
///   只创建一条 ChatCommandRecord（AgentInstanceId = dispatches[0].AgentId），
///   其余目标不执行。ChatMessageExecutionService.RunSecondaryChatFanoutAsync
///   完整多 Agent 并行流式 fanout 基础设施存在但未接入 ChatExecutionWorker 主循环。
///   <br/><b>修复：</b>为每个 targetAgentId 创建独立 ChatCommandRecord 并入队；
///   Worker 并行领取执行；fanoutIndex/fanoutCount 正确传递。
///   <br/><b>影响：</b>@all 广播退化为单 Agent 对话。
/// </description>
/// </item>
/// <item>
/// <description>
///   <b>ClientRequestId 应为必填</b><br/>
///   当前定义为 string?，后端用 Guid 回退。网络层重试（fetch 自动重试、浏览器恢复）
///   会在服务端产生两个不同命令，导致重复 execution。
///   <br/><b>修复：</b>前端 useChatState.ts 已生成 clientRequestId 并传入，
///   可改为 required；或保持 nullable 但记录警告日志。
///   <br/><b>影响：</b>极端网络条件下可能产生重复 Turn。
/// </description>
/// </item>
/// </list>
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

/// <summary>聊天消息响应（同步路径使用；命令队列路径返回 202 Accepted）。</summary>
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
