using PuddingCode.Diagnostics;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Diagnostics;

/// <summary>
/// 共享 Conversation 诊断事件投影器：
/// 把 canonical conversation_events 单事件投影为统一 Timeline 条目，
/// 并集中提供 Payload 有界解析 + 显式事件类型→状态映射。
/// 供 trace-report / RuntimeTimeline / E2E Evidence 三处共用，禁止三处各自解析 Payload。
/// </summary>
public interface IConversationDiagnosticEventProjector
{
    // ── 核心单事件投影 ─────────────────────────────
    /// <summary>把一个 ConversationEvent 投影为 RuntimeTimelineItemDto。</summary>
    RuntimeTimelineItemDto Project(ConversationEvent evt);

    /// <summary>批量投影（保持输入顺序）。</summary>
    IReadOnlyList<RuntimeTimelineItemDto> Project(IEnumerable<ConversationEvent> events);

    // ── 状态 / 终态映射 ─────────────────────────────
    /// <summary>显式事件类型 → Timeline Status（§5 完整映射表）。未知类型返回 "recorded"。</summary>
    string MapStatus(string eventType);

    /// <summary>事件类型是否为「终态事件」——用于决定 CompletedAt 是否 = OccurredAt。</summary>
    bool IsTerminalType(string eventType);

    // ── 有界 Payload 解析（三读者复用，禁止各自解析）──────────
    /// <summary>有界提取 Summary（§6 规则）。</summary>
    string? ExtractSummary(ConversationEvent evt);

    /// <summary>有界提取 Error（§6 规则）。</summary>
    string? ExtractError(ConversationEvent evt);

    // ── 聚合辅助（trace-report 复用）─────────────────
    /// <summary>从 usage.recorded payload 有界解析 token/模型/端点信息（供 LlmCallEntry）。</summary>
    UsageProjection? TryProjectUsage(ConversationEvent evt);

    /// <summary>从 tool.call.* payload 有界解析工具名 / 出口码 / 错误（供 ToolCallEntry）。</summary>
    ToolCallProjection? TryProjectToolCall(ConversationEvent evt);

    /// <summary>从 subagent.* payload / envelope 有界解析子代理 id（供 SubAgentTraceEntry）。</summary>
    string? ExtractSubAgentId(ConversationEvent evt);
}

/// <summary>usage.recorded 的有界投影（避免暴露 JsonElement / 原始 Payload）。</summary>
public sealed record UsageProjection(
    string? ProviderId, string? ModelId, string? Endpoint,
    long? InputTokens, long? OutputTokens, long? TotalTokens, long? DurationMs);

/// <summary>tool.call.* 的有界投影。</summary>
public sealed record ToolCallProjection(
    string? ToolName, int? ExitCode, string? Output, string? Error);
